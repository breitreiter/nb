// Line editor for nb — wraps vendored tonerdo/readline KeyHandler
// Adds: backslash continuation, history, guard character callbacks, command disambiguation

namespace nb.LineEditor;

public record SlashCommand(string Name, string Description);

public class NbLineEditor
{
    private readonly List<string> _history = new();

    /// <summary>
    /// Available slash commands for "/" disambiguation.
    /// </summary>
    public List<SlashCommand> Commands { get; set; } = new();

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

        // "/" guard: enter command disambiguation mode
        if (keyInfo.KeyChar == '/' && Commands.Count > 0)
        {
            var result = HandleSlashMode(prompt);
            return result;
        }

        while (keyInfo.Key != ConsoleKey.Enter)
        {
            handler.Handle(keyInfo);
            keyInfo = Console.ReadKey(true);
        }

        Console.WriteLine();

        var text = handler.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private string? HandleSlashMode(string prompt)
    {
        var typed = "";
        ShowCommandHints(typed);

        while (true)
        {
            var keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.Escape)
            {
                ClearHints();
                // Re-prompt with clean line
                ClearCurrentLine(prompt);
                Console.Write(prompt);
                return ReadSingleLine(prompt) is { } resumed ? resumed : null;
            }

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                ClearHints();
                Console.WriteLine();
                var cmd = "/" + typed;
                return string.IsNullOrWhiteSpace(typed) ? null : cmd;
            }

            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (typed.Length == 0)
                {
                    // Backspace past the "/" — cancel
                    ClearHints();
                    ClearCurrentLine(prompt);
                    Console.Write(prompt);
                    return ReadSingleLine(prompt) is { } resumed2 ? resumed2 : null;
                }
                typed = typed[..^1];
                // Redraw the input line
                ClearHints();
                ClearCurrentLine(prompt);
                Console.Write(prompt + "/" + typed);
                ShowCommandHints(typed);
                continue;
            }

            if (char.IsLetterOrDigit(keyInfo.KeyChar) || keyInfo.KeyChar == '_' || keyInfo.KeyChar == '-')
            {
                typed += keyInfo.KeyChar;

                var matches = Commands
                    .Where(c => c.Name.StartsWith("/" + typed, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 1)
                {
                    // Disambiguated — auto-execute
                    ClearHints();
                    ClearCurrentLine(prompt);
                    Console.Write(prompt + matches[0].Name);
                    Console.WriteLine();
                    return matches[0].Name;
                }

                if (matches.Count == 0)
                {
                    // No matches — just show what they typed, let them Enter or backspace
                    ClearHints();
                    ClearCurrentLine(prompt);
                    Console.Write(prompt + "/" + typed);
                    continue;
                }

                // Multiple matches — update display
                ClearHints();
                ClearCurrentLine(prompt);
                Console.Write(prompt + "/" + typed);
                ShowCommandHints(typed);
            }
        }
    }

    private void ShowCommandHints(string typed)
    {
        var matches = Commands
            .Where(c => c.Name.StartsWith("/" + typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return;

        // Save cursor position
        var savedLeft = Console.CursorLeft;
        var savedTop = Console.CursorTop;

        Console.WriteLine();
        foreach (var cmd in matches)
        {
            Console.WriteLine($"\u001b[90m  {cmd.Name,-12} {cmd.Description}\u001b[0m");
        }

        // Restore cursor
        Console.SetCursorPosition(savedLeft, savedTop);
    }

    private void ClearHints()
    {
        var savedLeft = Console.CursorLeft;
        var savedTop = Console.CursorTop;

        // Clear lines below cursor (max commands + 1 for the blank line)
        var linesToClear = Commands.Count + 1;
        for (int i = 1; i <= linesToClear; i++)
        {
            if (savedTop + i >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, savedTop + i);
            Console.Write(new string(' ', Console.WindowWidth));
        }

        Console.SetCursorPosition(savedLeft, savedTop);
    }

    private void ClearCurrentLine(string prompt)
    {
        // Move to start of input (after any ANSI codes in prompt, approximate with cursor position)
        Console.Write('\r');
        Console.Write(new string(' ', Console.WindowWidth));
        Console.Write('\r');
    }
}
