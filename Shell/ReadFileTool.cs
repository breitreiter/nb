using System.Text;
using Microsoft.Extensions.AI;

namespace nb.Shell;

public class ReadFileTool
{
    private const int DefaultMaxLines = 2000;

    private readonly ShellEnvironment _env;

    public ReadFileTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var readFunc = (string path, int? offset, int? limit) =>
            ReadFile(path, offset, limit);

        return AIFunctionFactory.Create(
            readFunc,
            name: "read_file",
            description: $"""
                Read the contents of a text file and return it with line numbers.
                Paths are relative to: {_env.ShellCwd}

                Parameters:
                - path: File path (absolute or relative to working directory)
                - offset: 1-based line number to start reading from (default: 1)
                - limit: Maximum number of lines to return (default: {DefaultMaxLines})

                Use this instead of bash cat/head/tail for reading files.
                For large files, use offset and limit to read specific sections.
                Returns numbered lines in "N: content" format.
                """
        );
    }

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

    public ReadFileResult ReadFile(string path, int? offset = null, int? limit = null)
    {
        try
        {
            var fullPath = ResolvePath(path);

            if (!File.Exists(fullPath))
                return new ReadFileResult(false, fullPath, null, 0, 0, $"File not found: {fullPath}");

            var allLines = File.ReadAllLines(fullPath);
            var totalLines = allLines.Length;

            var startLine = Math.Max(1, offset ?? 1);
            var maxLines = limit ?? DefaultMaxLines;

            // Clamp to file bounds
            if (startLine > totalLines)
                return new ReadFileResult(true, fullPath, "", totalLines, 0, null);

            var lines = allLines
                .Skip(startLine - 1)
                .Take(maxLines)
                .Select((line, i) => $"{startLine + i}: {line}");

            var content = string.Join("\n", lines);
            var linesReturned = Math.Min(maxLines, totalLines - startLine + 1);
            var truncated = startLine + linesReturned - 1 < totalLines;

            string? truncationNote = truncated
                ? $"[Showing lines {startLine}-{startLine + linesReturned - 1} of {totalLines}. Use offset/limit to read more.]"
                : null;

            if (truncationNote != null)
                content = $"{content}\n{truncationNote}";

            return new ReadFileResult(true, fullPath, content, totalLines, linesReturned, null);
        }
        catch (Exception ex)
        {
            return new ReadFileResult(false, path, null, 0, 0, ex.Message);
        }
    }
}

public record ReadFileResult(
    bool Success,
    string Path,
    string? Content,
    int TotalLines,
    int LinesReturned,
    string? Error);
