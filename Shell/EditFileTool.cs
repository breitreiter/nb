using Microsoft.Extensions.AI;

namespace nb.Shell;

public class EditFileTool
{
    private readonly ShellEnvironment _env;

    public EditFileTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var editFunc = (string path, string old_string, string new_string, bool? replace_all) =>
            EditFile(path, old_string, new_string, replace_all ?? false);

        return AIFunctionFactory.Create(
            editFunc,
            name: "edit_file",
            description: $"""
                Targeted string replacement in an existing file.
                Paths are relative to: {_env.ShellCwd}

                Parameters:
                - path: File path (absolute or relative to working directory)
                - old_string: Exact text to find (must match including whitespace and indentation)
                - new_string: Replacement text
                - replace_all: Replace all occurrences (default: false)

                Fails if old_string is not found. Fails if old_string is not unique when replace_all is false — add more surrounding context to make it unique.
                """
        );
    }

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

    public EditFileResult EditFile(string path, string oldString, string newString, bool replaceAll = false)
    {
        try
        {
            var fullPath = ResolvePath(path);

            if (!File.Exists(fullPath))
                return new EditFileResult(false, fullPath, 0, "File not found");

            if (oldString == newString)
                return new EditFileResult(false, fullPath, 0, "old_string and new_string are identical");

            var content = File.ReadAllText(fullPath);
            var occurrences = CountOccurrences(content, oldString);

            if (occurrences == 0)
                return new EditFileResult(false, fullPath, 0, "old_string not found in file");

            if (!replaceAll && occurrences > 1)
                return new EditFileResult(false, fullPath, 0,
                    $"old_string found {occurrences} times — provide more context to make it unique, or set replace_all to true");

            string newContent;
            int replacements;

            if (replaceAll)
            {
                newContent = content.Replace(oldString, newString);
                replacements = occurrences;
            }
            else
            {
                var index = content.IndexOf(oldString, StringComparison.Ordinal);
                newContent = string.Concat(content.AsSpan(0, index), newString, content.AsSpan(index + oldString.Length));
                replacements = 1;
            }

            File.WriteAllText(fullPath, newContent);

            return new EditFileResult(true, fullPath, replacements, null);
        }
        catch (Exception ex)
        {
            return new EditFileResult(false, path, 0, ex.Message);
        }
    }

    private static int CountOccurrences(string text, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(search, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}

public record EditFileResult(
    bool Success,
    string Path,
    int Replacements,
    string? Error);
