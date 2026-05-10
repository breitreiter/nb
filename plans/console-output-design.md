# Console Output Design Sketch

Status: Exploration

## Layers

Three distinct layers of console output, each with a different owner and purpose:

1. **Tool output** (app-generated, verbose) — the raw stuff: bash stdout, file contents, diffs, grep results
2. **Tool footers** (app-generated, terse) — one-liner summary after each tool completes
3. **Turn recaps** (model-generated) — narrative summary after multi-tool turns, prompted via system prompt

## Visual Structure of a Tool Block

```
  ── bash ─────────────────────────────────────
  │ $ dotnet build
  │ Microsoft (R) Build Engine version 17.8.3
  │ ... (build output) ...
  │ Build succeeded. 0 Warnings 0 Errors
  ── ✓ bash: dotnet build (0.8s, 52 lines) ────
```

```
  ── edit_file: src/Foo.cs ────────────────────
  │ - old line
  │ + new line
  │ Approve? [y/n/s]  y
  ── ✓ edit_file: src/Foo.cs (1 replacement) ──
```

```
  ── read_file: src/Bar.cs ────────────────────
  │ (142 lines)
  ── ✓ read_file: src/Bar.cs (142 lines) ──────
```

```
  ── grep: "TODO" in src/ ─────────────────────
  │ src/Foo.cs:12: // TODO fix this
  │ src/Bar.cs:34: // TODO refactor
  ── ✓ grep: "TODO" (2 matches, 2 files) ──────
```

## Approval Flows Inside Tool Blocks

The approval prompt lives inside the indented block. The footer comes after resolution:

```
  ── write_file: src/New.cs ───────────────────
  │ 34 lines, 1.2 KB
  │
  │ (preview on 's')
  │
  │ Write? [y/n/s]  y
  ── ✓ write_file: src/New.cs (34 lines) ──────
```

```
  ── bash ─────────────────────────────────────
  │ run: yarn install
  │
  │ Execute? [y/n/s]  n
  │ Reason: don't want to install right now
  ── ✗ bash: yarn install (rejected) ──────────
```

## Turn Recap (Model-Generated)

After a burst of tool calls, the model provides a narrative summary. This is prompted behavior, not app chrome:

```
  ── bash ─────────────────────────────────────
  │ ...
  ── ✓ bash: dotnet build (0.8s) ──────────────

  ── edit_file: src/Foo.cs ────────────────────
  │ ...
  ── ✓ edit_file: src/Foo.cs (1 replacement) ──

  ── write_file: src/Bar.cs ───────────────────
  │ ...
  ── ✓ write_file: src/Bar.cs (52 lines) ──────

  I added the FooService implementation and registered it in
  Program.cs. The build succeeds with no warnings.
```

The recaps are just normal model text — no special formatting. The tool footers provide the scannable "what happened" and the recap provides the "why."

## Implementation Notes

### Headers and Footers

Spectre.Console `Rule` is a good fit — horizontal line with embedded text, auto-sizes to terminal width:

```csharp
AnsiConsole.Write(new Rule("bash") { Style = "dim", Justification = Justify.Left });
// ... tool output ...
AnsiConsole.Write(new Rule("✓ bash: dotnet build (0.8s)") { Style = "dim", Justification = Justify.Left });
```

### Indented Body Content

DIY line prefixing. Spectre's `Panel`/`Padder` widgets render all-at-once and don't support streaming or interactive prompts mid-flow.

```csharp
// simple prefix for each line of tool output
void WriteIndented(string line) => Console.Write($"  │ {line}");
```

For multi-line content (bash output, diffs), split and re-prefix each line. For PxSharp/double-byte content that already goes through `Console.Write`, prefixing is straightforward.

### Things to Figure Out

- **Indent width**: 2 spaces + `│` + space = 4 chars. Enough? Too much?
- **Color**: Should the `│` gutter be dim/grey? Match the header Rule color?
- **Nesting**: If a tool call triggers sub-operations (e.g., bash that we classify), is there ever a second indent level? Probably not — keep it flat.
- **Long output**: The existing sandwich truncation (head + tail) still applies inside the indented block. Footer summarizes total lines so the user knows what was elided.
- **Terminal width**: `Rule` handles this. The `│` prefix is fixed. Content that wraps will look slightly off — probably fine, since it's already verbose output.
- **Thinking blocks**: Same structure as tools? Or lighter — maybe just dimmed text with no gutter, bookended by a Rule?

## Playground: Alternative Presentations

### Minimal — no gutter, just rules

```
  ── bash ─────────────────────────────────────
  $ dotnet build
  Build succeeded. 0 Warnings 0 Errors
  ── ✓ bash: dotnet build (0.8s, 52 lines) ────
```

### Gutter only, no top rule

```
  │ $ dotnet build
  │ Build succeeded. 0 Warnings 0 Errors
  ── ✓ bash: dotnet build (0.8s, 52 lines) ────
```

### Double-indent for nested context (approval details)

```
  ── bash ─────────────────────────────────────
  │ run: rm -rf node_modules
  │ ⚠ dangerous command
  │
  │ Execute? [y/n/s]  s
  │   $ rm -rf node_modules
  │ Execute? [y/n]  y
  ── ✓ bash: rm -rf node_modules (0.1s) ───────
```

### Colored status in footer

```
  ── ✓ bash: dotnet build (0.8s) ──────────────   (green ✓)
  ── ✗ bash: yarn install (rejected) ──────────   (red ✗)
  ── ✓ edit_file: src/Foo.cs (1 change) ───────   (green ✓)
  ── ○ read_file: src/Bar.cs (142 lines) ──────   (dim ○ for no-op/read-only)
```

### Compact footers for quiet tools

Some tools (read_file, find_files, grep) are read-only and low-drama. Maybe they get a single-line treatment with no header:

```
  ── ○ read_file: src/Bar.cs (142 lines) ──────
  ── ○ grep: "TODO" (2 matches) ───────────────
```

While mutating/interactive tools get the full header + body + footer:

```
  ── bash ─────────────────────────────────────
  │ $ dotnet test
  │ ...
  ── ✓ bash: dotnet test (4.2s, 87 lines) ─────
```
