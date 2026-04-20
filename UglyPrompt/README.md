# UglyPrompt

A no-frills readline-style console line editor for .NET. Backslash continuation, command completion hints, bracketed paste, and history — with no external dependencies.

A permissively-licensed alternative to [PrettyPrompt](https://github.com/waf/PrettyPrompt).

## Installation

```
dotnet add package UglyPrompt
```

## Usage

```csharp
var editor = new LineEditor();

while (true)
{
    string? input = editor.ReadLine(">> ");
    if (input == null) break; // EOF or Ctrl+C
    Console.WriteLine(input);
}
```

`ReadLine` returns `null` on EOF (Ctrl+D), Ctrl+C, or whitespace-only input. It handles backslash continuation internally — the returned string is the fully-joined multi-line value.

## Completion Hints

Populate `Commands` or `Kits` to enable hints for `/` and `+` prefixes:

```csharp
var editor = new LineEditor
{
    Commands =
    [
        new CompletionHint("/help",    "Show help"),
        new CompletionHint("/history", "Show conversation history"),
        new CompletionHint("/clear",   "Clear the screen"),
    ],
    Kits =
    [
        new CompletionHint("+core",  "Core tools"),
        new CompletionHint("+files", "File tools"),
    ],
};
```

When the current line starts with `/` or `+`, a terse comma-separated list of matching names is shown on an ephemeral line below the input and filtered as the user types. The hint is display-only — the user still types the full command and presses Enter to submit. Editing (arrows, history, Ctrl+U/W, etc.) works normally throughout.

## Features

- **History** — Up/Down arrows or Ctrl+P/N to navigate previous inputs
- **Backslash continuation** — Lines ending with `\` prompt for more input; the full value is returned joined with newlines
- **Bracketed paste** — Handles terminal paste sequences including embedded newlines
- **Standard line editing** — Home/End, Ctrl+A/E, Ctrl+U/K/W, Ctrl+T, and more (see below)
- **No dependencies** — Only the .NET standard library; targets .NET 8.0

## Keyboard Shortcuts

| Keys | Action |
|------|--------|
| Left / Ctrl+B | Move cursor left |
| Right / Ctrl+F | Move cursor right |
| Home / Ctrl+A | Move to start of line |
| End / Ctrl+E | Move to end of line |
| Backspace / Ctrl+H | Delete character before cursor |
| Delete / Ctrl+D | Delete character at cursor (EOF if line empty) |
| Ctrl+U | Delete to start of line |
| Ctrl+K | Delete to end of line |
| Ctrl+W | Delete word before cursor |
| Ctrl+T | Transpose characters |
| Ctrl+L / Escape | Clear line |
| Up / Ctrl+P | Previous history entry |
| Down / Ctrl+N | Next history entry |

## License

MIT
