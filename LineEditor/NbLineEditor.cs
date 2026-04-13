// Line editor for nb — wraps vendored tonerdo/readline KeyHandler
// Adds: backslash continuation, history, guard character callbacks

namespace nb.LineEditor;

public class NbLineEditor
{
    private readonly List<string> _history = new();

    /// <summary>
    /// Called when the user types '+' as the only input and presses Enter.
    /// Return the string to use as input, or null to re-prompt.
    /// </summary>
    public Func<string?>? OnPlusGuard { get; set; }

    /// <summary>
    /// Read a line of input with history, backslash continuation, and guard characters.
    /// Returns null if the user enters empty input (after trimming).
    /// </summary>
    public string? ReadLine(string prompt)
    {
        var line = ReadSingleLine(prompt);
        if (line == null) return null;

        // Guard character: + as sole input
        if (line.Trim() == "+" && OnPlusGuard != null)
        {
            var result = OnPlusGuard();
            return result;
        }

        // Backslash continuation
        if (line.EndsWith('\\'))
        {
            var lines = new List<string> { line[..^1] };
            while (true)
            {
                var continuation = ReadSingleLine("  > ");
                if (continuation == null)
                {
                    lines.Add("");
                    break;
                }
                if (continuation.EndsWith('\\'))
                {
                    lines.Add(continuation[..^1]);
                }
                else
                {
                    lines.Add(continuation);
                    break;
                }
            }
            line = string.Join("\n", lines);
        }

        if (string.IsNullOrWhiteSpace(line)) return null;

        _history.Add(line.Contains('\n') ? line.Split('\n')[0] + "..." : line);
        return line;
    }

    private string? ReadSingleLine(string prompt)
    {
        Console.Write(prompt);

        var handler = new KeyHandler(new ConsoleAdapter(), _history);

        var keyInfo = Console.ReadKey(true);
        while (keyInfo.Key != ConsoleKey.Enter)
        {
            handler.Handle(keyInfo);
            keyInfo = Console.ReadKey(true);
        }

        Console.WriteLine();

        var text = handler.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
