using Microsoft.Extensions.AI;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace nb.Shell;

public class FindFilesTool
{
    private const int DefaultMaxResults = 200;

    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget"
    };

    private readonly ShellEnvironment _env;

    public FindFilesTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var findFunc = (string pattern, string path, int? max_results) =>
            FindFiles(pattern, string.IsNullOrEmpty(path) ? null : path, max_results);

        return AIFunctionFactory.Create(
            findFunc,
            name: "find_files",
            description: $"""
                Find files matching a glob pattern. Returns relative paths sorted alphabetically.
                Searches from: {_env.ShellCwd}

                Parameters:
                - pattern: Glob pattern (e.g. "**/*.cs", "src/**/*.ts", "*.json")
                - path: Directory to search in (absolute or relative to working directory). Empty string or omit for working directory.
                - max_results: Maximum number of results to return (default: {DefaultMaxResults})

                Automatically skips: {string.Join(", ", SkipDirectories)}
                Use this instead of bash find/ls/dir for file discovery.
                """
        );
    }

    public FindFilesResult FindFiles(string pattern, string? path = null, int? maxResults = null)
    {
        try
        {
            var searchDir = string.IsNullOrEmpty(path)
                ? _env.ShellCwd
                : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

            if (!Directory.Exists(searchDir))
                return new FindFilesResult(false, Array.Empty<string>(), 0, $"Directory not found: {searchDir}");

            var limit = maxResults ?? DefaultMaxResults;

            var matcher = new Matcher();
            matcher.AddInclude(pattern);

            // Add exclusions for common directories
            foreach (var dir in SkipDirectories)
            {
                matcher.AddExclude($"{dir}/**");
            }

            var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchDir));
            var result = matcher.Execute(directoryInfo);

            var files = result.Files
                .Select(f => f.Path)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var totalMatches = files.Length;
            var truncated = totalMatches > limit;
            var returnedFiles = truncated ? files.Take(limit).ToArray() : files;

            var output = string.Join("\n", returnedFiles);
            if (truncated)
                output += $"\n\n[Showing {limit} of {totalMatches} matches. Use max_results to see more.]";

            return new FindFilesResult(true, returnedFiles, totalMatches, null, output);
        }
        catch (Exception ex)
        {
            return new FindFilesResult(false, Array.Empty<string>(), 0, ex.Message);
        }
    }
}

public record FindFilesResult(
    bool Success,
    string[] Files,
    int TotalMatches,
    string? Error,
    string? Output = null);
