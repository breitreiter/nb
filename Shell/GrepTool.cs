using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace nb.Shell;

public class GrepTool
{
    private const int DefaultMaxResults = 100;
    private const int MaxLineLength = 200;
    private const int BinaryCheckBytes = 8192;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget"
    };

    private readonly ShellEnvironment _env;

    public GrepTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var grepFunc = (string pattern, string path, string file_pattern, bool? case_insensitive, int? max_results) =>
            Grep(pattern, string.IsNullOrEmpty(path) ? null : path, string.IsNullOrEmpty(file_pattern) ? null : file_pattern, case_insensitive, max_results);

        return AIFunctionFactory.Create(
            grepFunc,
            name: "grep",
            description: $"""
                Search file contents using regex. Returns matching lines with file paths and line numbers.
                Searches from: {_env.ShellCwd}

                Parameters:
                - pattern: Regular expression to search for
                - path: Directory or file to search (absolute or relative to working directory). Empty string for working directory.
                - file_pattern: Glob filter for files to search (e.g. "*.cs", "*.ts"). Empty string for all files.
                - case_insensitive: If true, perform case-insensitive search (default: false)
                - max_results: Maximum number of matching lines to return (default: {DefaultMaxResults})

                Returns results in "file:line_number: content" format.
                Automatically skips binary files and common non-source directories.
                Use this instead of bash grep/findstr/Select-String for content search.
                """
        );
    }

    public GrepResult Grep(string pattern, string? path = null, string? filePattern = null, bool? caseInsensitive = null, int? maxResults = null)
    {
        try
        {
            var searchPath = string.IsNullOrEmpty(path)
                ? _env.ShellCwd
                : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

            var limit = maxResults ?? DefaultMaxResults;
            var regexOptions = RegexOptions.Compiled;
            if (caseInsensitive == true)
                regexOptions |= RegexOptions.IgnoreCase;

            Regex regex;
            try
            {
                regex = new Regex(pattern, regexOptions);
            }
            catch (RegexParseException ex)
            {
                return new GrepResult(false, Array.Empty<string>(), 0, $"Invalid regex pattern: {ex.Message}");
            }

            var matches = new List<string>();
            var totalMatches = 0;

            // If searching a single file
            if (File.Exists(searchPath))
            {
                SearchFile(searchPath, Path.GetDirectoryName(searchPath) ?? searchPath, regex, matches, ref totalMatches, limit);
                return BuildResult(matches, totalMatches, limit);
            }

            if (!Directory.Exists(searchPath))
                return new GrepResult(false, Array.Empty<string>(), 0, $"Path not found: {searchPath}");

            // Get files to search
            IEnumerable<string> files;
            if (!string.IsNullOrEmpty(filePattern))
            {
                var matcher = new Matcher();
                matcher.AddInclude($"**/{filePattern}");
                foreach (var dir in SkipDirectories)
                    matcher.AddExclude($"{dir}/**");

                var dirInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchPath));
                var globResult = matcher.Execute(dirInfo);
                files = globResult.Files.Select(f => Path.Combine(searchPath, f.Path));
            }
            else
            {
                files = EnumerateFilesRecursive(searchPath);
            }

            foreach (var file in files)
            {
                if (totalMatches >= limit) break;
                SearchFile(file, searchPath, regex, matches, ref totalMatches, limit);
            }

            return BuildResult(matches, totalMatches, limit);
        }
        catch (Exception ex)
        {
            return new GrepResult(false, Array.Empty<string>(), 0, ex.Message);
        }
    }

    private static void SearchFile(string filePath, string baseDir, Regex regex, List<string> matches, ref int totalMatches, int limit)
    {
        try
        {
            if (IsBinaryFile(filePath))
                return;

            var relativePath = Path.GetRelativePath(baseDir, filePath);
            var lines = File.ReadLines(filePath);
            var lineNumber = 0;

            foreach (var line in lines)
            {
                lineNumber++;
                if (regex.IsMatch(line))
                {
                    totalMatches++;
                    if (matches.Count < limit)
                    {
                        var displayLine = line.Length > MaxLineLength
                            ? line[..MaxLineLength] + "..."
                            : line;
                        matches.Add($"{relativePath}:{lineNumber}: {displayLine}");
                    }
                }
            }
        }
        catch
        {
            // Skip files that can't be read
        }
    }

    private static bool IsBinaryFile(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[Math.Min(BinaryCheckBytes, stream.Length)];
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            return Array.IndexOf(buffer, (byte)0, 0, bytesRead) >= 0;
        }
        catch
        {
            return true; // Can't read = skip
        }
    }

    private static IEnumerable<string> EnumerateFilesRecursive(string directory)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
                yield return file;

            try
            {
                foreach (var subDir in Directory.GetDirectories(dir))
                {
                    var dirName = Path.GetFileName(subDir);
                    if (!SkipDirectories.Contains(dirName))
                        stack.Push(subDir);
                }
            }
            catch
            {
                // Skip directories we can't enumerate
            }
        }
    }

    private static GrepResult BuildResult(List<string> matches, int totalMatches, int limit)
    {
        var output = string.Join("\n", matches);
        if (totalMatches > limit)
            output += $"\n\n[Showing {limit} of {totalMatches} matches. Use max_results to see more.]";

        return new GrepResult(true, matches.ToArray(), totalMatches, null, output);
    }
}

public record GrepResult(
    bool Success,
    string[] Matches,
    int TotalMatches,
    string? Error,
    string? Output = null);
