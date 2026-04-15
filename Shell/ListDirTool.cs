using Microsoft.Extensions.AI;

namespace nb.Shell;

public class ListDirTool
{
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", "__pycache__",
        ".venv", "venv", ".idea", "dist", "build", ".next", ".nuget"
    };

    private readonly ShellEnvironment _env;

    public ListDirTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var listFunc = (string path) =>
            ListDir(string.IsNullOrEmpty(path) ? null : path);

        return AIFunctionFactory.Create(
            listFunc,
            name: "list_dir",
            description: $"""
                List the contents of a directory. Returns files and subdirectories with type indicators.
                Paths are relative to: {_env.ShellCwd}

                Parameters:
                - path: Directory path (absolute or relative to working directory). Empty string for working directory.

                Returns entries in "type name" format where type is [file] or [dir].
                Automatically skips: {string.Join(", ", SkipDirectories)}
                """
        );
    }

    public string GetCwd() => _env.ShellCwd;

    public string ResolvePath(string? path) => string.IsNullOrEmpty(path)
        ? _env.ShellCwd
        : Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

    public ListDirResult ListDir(string? path = null)
    {
        try
        {
            var dirPath = ResolvePath(path);

            if (!Directory.Exists(dirPath))
                return new ListDirResult(false, dirPath, null, $"Directory not found: {dirPath}");

            var entries = new List<string>();

            foreach (var dir in Directory.GetDirectories(dirPath).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (!SkipDirectories.Contains(name))
                    entries.Add($"[dir]  {name}");
            }

            foreach (var file in Directory.GetFiles(dirPath).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add($"[file] {Path.GetFileName(file)}");
            }

            var output = entries.Count > 0
                ? string.Join("\n", entries)
                : "(empty directory)";

            return new ListDirResult(true, dirPath, output, null);
        }
        catch (Exception ex)
        {
            return new ListDirResult(false, path ?? _env.ShellCwd, null, ex.Message);
        }
    }
}

public record ListDirResult(
    bool Success,
    string Path,
    string? Output,
    string? Error);
