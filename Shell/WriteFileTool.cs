using Microsoft.Extensions.AI;

namespace nb.Shell;

public class WriteFileTool
{
    private readonly ShellEnvironment _env;

    public WriteFileTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var writeFunc = (string path, string content) =>
            WriteFile(path, content);

        return AIFunctionFactory.Create(
            writeFunc,
            name: "write_file",
            description: $"""
                Create a new file or completely rewrite an existing file.
                Paths are relative to: {_env.ShellCwd}

                Parameters:
                - path: File path (absolute or relative to working directory)
                - content: The full content to write to the file

                Use this for creating new files or complete rewrites.
                For targeted edits to existing files, use edit_file instead.
                File writes require user approval.
                """
        );
    }

    public string GetCwd() => _env.ShellCwd;

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

    public WriteFileResult WriteFile(string path, string content)
    {
        try
        {
            // Resolve path relative to shell cwd
            var fullPath = Path.IsPathRooted(path)
                ? path
                : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

            // Ensure parent directory exists
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content);

            return new WriteFileResult(
                Success: true,
                Path: fullPath,
                BytesWritten: System.Text.Encoding.UTF8.GetByteCount(content),
                Error: null
            );
        }
        catch (Exception ex)
        {
            return new WriteFileResult(
                Success: false,
                Path: path,
                BytesWritten: 0,
                Error: ex.Message
            );
        }
    }
}

public record WriteFileResult(
    bool Success,
    string Path,
    int BytesWritten,
    string? Error);
