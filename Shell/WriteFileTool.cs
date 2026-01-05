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
                Write content to a file. Creates the file if it doesn't exist, overwrites if it does.
                Paths are relative to: {_env.ShellCwd}

                Parameters:
                - path: File path (absolute or relative to working directory)
                - content: The content to write to the file

                Use this instead of bash heredocs or echo redirects for writing files.
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
