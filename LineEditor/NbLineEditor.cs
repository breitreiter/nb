// Line editor for nb — wraps vendored tonerdo/readline KeyHandler
// Adds: backslash continuation, history, guard char disambiguation for / and +

namespace nb.LineEditor;

public record SlashCommand(string Name, string Description);

public class NbLineEditor
{
    private readonly List<string> _history = new();

    /// <summary>Available slash commands for "/" disambiguation.</summary>
    public List<SlashCommand> Commands { get; set; } = new();

    /// <summary>Available kits for "+" disambiguation.</summary>
    public List<SlashCommand> Kits { get; set; } = new();

    /// <summary>Called when a kit is selected via "+" disambiguation. Receives kit name (e.g. "+review").</summary>
    public Action<string>? OnKitSelected { get; set; }

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
                var continuation = ReadSingleLine("  › ");
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

    private string? ReadSingleLine(string prompt)
    {
        Console.Write(prompt);

        var handler = new KeyHandler(new ConsoleAdapter(), _history);
        var keyInfo = Console.ReadKey(true);

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
            var selected = HandleGuardMode(prompt, "+", Kits);
            if (selected != null && selected.StartsWith("+"))
            {
                OnKitSelected?.Invoke(selected);
                return null; // kit activation handled by callback, don't send to LLM
            }
            return selected;
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

    private string? HandleGuardMode(string prompt, string prefix, List<SlashCommand> items)
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
                    continue; // stay in menu
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
                // Check for exact match (e.g. "//")
                var exact = items.FirstOrDefault(c => c.Name.Equals(prefix + typed, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                {
                    ClearHints(items.Count);
                    ClearCurrentLine();
                    Console.Write(prompt);
                    return null; // cancel, back to prompt
                }
            }

            if (char.IsLetterOrDigit(keyInfo.KeyChar) || keyInfo.KeyChar == '_' || keyInfo.KeyChar == '-')
            {
                typed += keyInfo.KeyChar;

                var matches = items
                    .Where(c => c.Name.StartsWith(prefix + typed, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matches.Count == 1)
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

    private void ShowHints(string prefix, string typed, List<SlashCommand> items)
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
            var shortName = cmd.Name[prefix.Length..]; // strip / or +
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
