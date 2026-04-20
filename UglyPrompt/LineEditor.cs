// UglyPrompt — a no-frills readline-style line editor for .NET console apps
// Wraps vendored tonerdo/readline KeyHandler (MIT License)
// Adds: backslash continuation, history, guard char disambiguation for / and +

namespace UglyPrompt;

public record CompletionHint(string Name, string Description);

public class LineEditor
{
    private readonly List<string> _history = new();

    public List<CompletionHint> Commands { get; set; } = new();
    public List<CompletionHint> Kits { get; set; } = new();

    /// <summary>
    /// When true (default), typing enough chars to uniquely identify a completion auto-selects it.
    /// Set to false to require Enter to confirm.
    /// </summary>
    public bool QuickComplete { get; set; } = true;

    public string? ReadLine(string prompt)
    {
        var line = ReadSingleLine(prompt);
        if (line == null) return null;

        // Backslash continuation
        if (line.EndsWith('\\'))
        {
            var lines = new List<string> { line[..^1] };
            while (true)
            {
                var continuation = ReadSingleLine("  ", enableGuards: false);
                if (continuation == null)
                {
                    lines.Add("");
                    break;
                }
                if (continuation.EndsWith('\\'))
                    lines.Add(continuation[..^1]);
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

    private string? ReadSingleLine(string prompt, bool enableGuards = true)
    {
        Console.Write(prompt);

        var handler = new KeyHandler(new ConsoleAdapter(), _history);
        var keyInfo = Console.ReadKey(true);

        if (enableGuards)
        {
            // "/" guard: command disambiguation
            if (keyInfo.KeyChar == '/' && Commands.Count > 0)
                return HandleGuardMode(prompt, "/", Commands);

            // "+" guard: kit disambiguation
            if (keyInfo.KeyChar == '+')
            {
                if (Kits.Count == 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("\u001b[90m  No kits configured. Add kits to kits.json\u001b[0m");
                    return null;
                }
                return HandleGuardMode(prompt, "+", Kits);
            }
        }

        while (true)
        {
            // Bracketed paste: ESC [ 2 0 0 ~ ... content ... ESC [ 2 0 1 ~
            if (keyInfo.Key == ConsoleKey.Escape && Console.KeyAvailable)
            {
                var pasted = TryReadBracketedPaste();
                if (pasted != null)
                {
                    handler.InsertText(pasted);
                    keyInfo = Console.ReadKey(true);
                    continue;
                }
                // Not a paste sequence — fall through to handler (clears line)
            }

            if (keyInfo.Key == ConsoleKey.Enter)
            {
                // On Windows without bracketed paste, pasted newlines arrive as Enter keys.
                // If more input is buffered immediately after Enter, it's a paste — keep reading.
                if (Console.KeyAvailable)
                {
                    handler.InsertText("\n");
                    keyInfo = Console.ReadKey(true);
                    continue;
                }
                break;
            }

            // Guard mode re-entry: handles slash/kit trigger after backspacing to empty
            if (enableGuards && handler.Text.Length == 0)
            {
                if (keyInfo.KeyChar == '/' && Commands.Count > 0)
                {
                    ClearCurrentLine();
                    Console.Write(prompt);
                    return HandleGuardMode(prompt, "/", Commands);
                }
                if (keyInfo.KeyChar == '+')
                {
                    ClearCurrentLine();
                    Console.Write(prompt);
                    if (Kits.Count == 0)
                    {
                        Console.WriteLine();
                        Console.WriteLine("\u001b[90m  No kits configured. Add kits to kits.json\u001b[0m");
                        return null;
                    }
                    return HandleGuardMode(prompt, "+", Kits);
                }
            }

            handler.Handle(keyInfo);
            keyInfo = Console.ReadKey(true);
        }

        Console.WriteLine();
        var text = handler.Text;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    // Try to read a bracketed paste sequence. Call after consuming \x1b.
    // Returns the paste content if \x1b[200~ was present, null otherwise.
    private static string? TryReadBracketedPaste()
    {
        var prefix = new char[5];
        for (int i = 0; i < 5; i++)
        {
            if (!Console.KeyAvailable) return null;
            prefix[i] = Console.ReadKey(true).KeyChar;
        }
        if (new string(prefix) != "[200~") return null;

        var content = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.Escape)
            {
                var end = new char[5];
                for (int i = 0; i < 5; i++)
                    end[i] = Console.ReadKey(true).KeyChar;
                if (new string(end) == "[201~") break;
                content.Append('\x1b');
                content.Append(new string(end));
            }
            else if (k.Key == ConsoleKey.Enter)
            {
                content.Append('\n');
            }
            else
            {
                content.Append(k.KeyChar);
            }
        }
        return content.ToString();
    }

    private string? HandleGuardMode(string prompt, string prefix, List<CompletionHint> items)
    {
        var typed = "";
        ShowHints(prefix, typed, items);

        while (true)
        {
            var keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.Escape || keyInfo.Key == ConsoleKey.Enter)
            {
                ClearHints(items.Count);
                Console.WriteLine();
                return string.IsNullOrWhiteSpace(typed) ? null : prefix + typed;
            }

            if (keyInfo.Key == ConsoleKey.Backspace)
            {
                if (typed.Length == 0)
                    continue;
                typed = typed[..^1];
                ClearHints(items.Count);
                ClearCurrentLine();
                Console.Write(prompt + prefix + typed);
                ShowHints(prefix, typed, items);
                continue;
            }

            if (keyInfo.KeyChar == prefix[0]) // e.g. "/" in slash mode = "//" cancel
            {
                typed += keyInfo.KeyChar;
                var exact = items.FirstOrDefault(c => c.Name.Equals(prefix + typed, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    ClearHints(items.Count);
                    ClearCurrentLine();
                    Console.Write(prompt);
                    return null;
                }
            }

            if (char.IsLetterOrDigit(keyInfo.KeyChar) || keyInfo.KeyChar == '_' || keyInfo.KeyChar == '-')
            {
                typed += keyInfo.KeyChar;

                var matches = items
                    .Where(c => c.Name.StartsWith(prefix + typed, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 1 && QuickComplete)
                {
                    ClearHints(items.Count);
                    ClearCurrentLine();
                    Console.Write(prompt + matches[0].Name);
                    Console.WriteLine();
                    return matches[0].Name;
                }

                ClearHints(items.Count);
                ClearCurrentLine();
                Console.Write(prompt + prefix + typed);

                if (matches.Count > 0)
                    ShowHints(prefix, typed, items);
            }
        }
    }

    private void ShowHints(string prefix, string typed, List<CompletionHint> items)
    {
        var matches = items
            .Where(c => c.Name.StartsWith(prefix + typed, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return;

        var savedLeft = Console.CursorLeft;
        var savedTop = Console.CursorTop;

        Console.WriteLine();
        foreach (var cmd in matches)
        {
            var shortName = cmd.Name[prefix.Length..];
            var pad = new string(' ', Math.Max(0, 14 - shortName.Length));
            Console.WriteLine($"\u001b[90m  \u001b[97m{shortName[0]}\u001b[90m{shortName[1..]}{pad}{cmd.Description}\u001b[0m");
        }

        Console.SetCursorPosition(savedLeft, savedTop);
    }

    private void ClearHints(int maxLines)
    {
        var savedLeft = Console.CursorLeft;
        var savedTop = Console.CursorTop;

        for (int i = 1; i <= maxLines + 1; i++)
        {
            if (savedTop + i >= Console.BufferHeight) break;
            Console.SetCursorPosition(0, savedTop + i);
            Console.Write(new string(' ', Console.WindowWidth));
        }

        Console.SetCursorPosition(savedLeft, savedTop);
    }

    private void ClearCurrentLine()
    {
        Console.Write('\r');
        Console.Write(new string(' ', Console.WindowWidth));
        Console.Write('\r');
    }
}
