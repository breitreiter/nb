---
kind: plan
title: Headless machine-friendly output (tool-call logs)
created: 2026-07-06
updated: 2026-07-06
status: current
state: exploring
touches:
  files:
    - Program.cs
    - ConversationManager.cs
    - Utilities/MarkdownRenderer.cs
  features: [headless, scripting, output]
provenance:
  author: claude
---

# Headless machine-friendly output (tool-call logs)

## Why this plan exists

nb is being driven as a one-shot eval harness:

```
cd /tmp/<guid> && OUT=$(nb --trust "<prompt>")
```

The caller needs to parse two things out of `$OUT`: (a) the model's **final
answer** (typically a ```` ```json ```` block the prompt asked for) and (b) the
**sequence of tool calls** the model made. Today nb emits its full interactive
TUI down a pipe — Spectre markup, 256-color ANSI, code-fence dividers and glyph
bullets — which is hostile to a programmatic consumer. There is **no headless
path at all**: every surface writes through the one interactive renderer.

This pairs with `bugs/shell-tool-no-filesystem-sandbox.md` as the second half of
"make nb usable as a headless harness" — that bug is about *safely* running
untrusted model commands in a throwaway dir; this plan is about getting a
*parseable* transcript back out.

This is a **design/plan doc only** — no code here.

## What actually happens today (verified against source)

**TTY detection is one-directional.** nb checks `Console.IsInputRedirected`
(`Program.cs:454`, `Program.cs:484`) to decide whether to read piped stdin and
whether to enable bracketed paste. It **never checks
`Console.IsOutputRedirected`** — grep confirms zero references anywhere. So the
output side has no idea it's talking to a pipe.

**There is no output-mode flag.** `ParseFlags` (`Program.cs:212-258`) handles
`--approve`, `--system`, `--nobash`, `--verbose`, `--trust`, `--dump-tools`,
`--debug-stream`, `--no-kits`, `--help`. There is no `--json`, `--porcelain`,
`--headless`, `--quiet`, or `--no-color`. `--verbose` (`Program.cs:228`) only
*adds* tool input/output logging — it makes the output noisier, not cleaner.

**Everything renders through Spectre's global `AnsiConsole`.** No custom profile
is configured — grep finds no `AnsiConsole.Create`, `AnsiConsoleSettings`, or
`ColorSystem`/`Profile` override. So color gating is left entirely to Spectre's
own capability auto-detection. Two consequences worth stating plainly:

1. The 256-color codes seen in the eval capture (`\x1b[38;5;8m` = grey,
   `\x1b[38;5;9m` = bright red) are Spectre rendering named theme colors
   (`UIColors` maps `Muted → grey`, `Error → red`, etc., `Utilities/UIColors.cs:40-59`).
   That they survive into a `$(...)` capture means the harness's stdout is being
   treated as ANSI-capable. **We cannot rely on Spectre's auto-detect to strip
   color for us** — it already "auto-detects" and the bytes still come through.
   The trigger for headless mode must therefore be *ours* (see below), not
   delegated to Spectre.
2. Even if color were stripped, **the structural problems remain** — stripping
   ANSI does not un-transform a code fence or make the tool bullets a stable
   contract. Headless mode is a different *render path*, not just "color off."

### The concrete pain points, with anchors

- **Trust preamble.** `Program.cs:416` emits `Trust mode active — auto-approving
  within <cwd>` on every `--trust` run. Pure chrome to a parser.

- **"Thinking..." spinner + streamed reasoning.** The TTFB spinner is
  `AnsiConsole.Status().Spinner(...).StartAsync("Thinking...", ...)` at
  `ConversationManager.cs:268-271`. Reasoning/prose then streams incrementally
  through a `MarkdownRenderer` (`ConversationManager.cs:260, 281, 288`).

- **The fenced answer block becomes a boxed divider — the biggest pain.**
  `MarkdownRenderer.RenderLine` turns an opening ```` ``` ```` fence into a
  Spectre `Rule` with the language as its title (`Utilities/MarkdownRenderer.cs:76-86`),
  writes the body lines raw (`:71`), and closes with another `Rule` (`:64-66`).
  So a model answer of

  ````
  ```json
  {"root_cause": "..."}
  ```
  ````

  renders on the wire as `── json ─────…` / `{...}` / `─────…` — all
  ANSI-colored, the literal fence **destroyed**. A consumer cannot grab the
  fenced block by its delimiters; the eval harness had to fall back to
  brace-scanning ANSI-stripped text. `IsFence` is `Utilities/MarkdownRenderer.cs:107-111`.

- **Tool calls are glyph bullets, not a contract.** Each tool prints a
  `• <verb>` line and a `→`/`✓`/`✗` result line via `AnsiConsole.MarkupLine`,
  scattered across `ConversationManager.cs`:
  - bash: `• bash:` / `• bash (pre-approved):` / `• auto: bash` —
    `ConversationManager.cs:1035, 1028, 1046`; exit footer `✓/✗ exit N` —
    `:1236-1238`.
  - read_file: `• reading <path>` `:392`; results `→ image (...)` / `→ <label>`
    `:406, 420`.
  - find_files: `• find_files:` `:456`; `→ N files` `:462`.
  - grep: `• grep:` `:496`; `→ N matches` `:504`.
  - list_dir: `• list_dir:` `:530`; `→ N entries` `:537`.
  - todo: `• todo_write (N change(s))` `:579`; `• todo_read` `:586`.
  - write_file: `• write <path> (N lines)` `:1280`; `✓ wrote N bytes` `:1289`.
  - generic MCP/fake: `• calling <name>` `:340, 685`; `🎭 Fake tool invoked` `:606`.

  The verb, the target, and the result are spread across two lines and encoded in
  glyphs and prose — fine for a human, unstable for a parser.

## Proposal

### Trigger — explicit flag, primary; auto-detect as a secondary convenience

Recommend an **explicit flag** as the contract:

```
nb --porcelain "<prompt>"          # stable text lines (phase 1)
nb --output=jsonl "<prompt>"       # JSONL event stream (phase 2)
```

Why explicit over auto: scripts want a **predictable** contract independent of
where stdout happens to point, and — as established above — nb's only current
color gate (Spectre auto-detect) already fails to strip in the harness's capture
path, so `Console.IsOutputRedirected` alone is not a reliable signal here.
Explicit also lets a human intentionally pipe rich output into `less -R` without
losing color.

Do also, cheaply and independently, gate **color** on `Console.IsOutputRedirected`
and the `NO_COLOR` env convention — that's the zero-config win and it's correct
regardless of mode. But the structural changes (verbatim fences, stable tool
lines) ride on the **explicit flag**, because they change semantics a human piping
to `less` would not want.

Parse the new flag alongside the others in `ParseFlags` (`Program.cs:212-258`),
storing an `OutputMode { Interactive, Porcelain, Jsonl }` enum next to the
existing static flags (`Program.cs:50-55`).

### What the mode changes

1. **No ANSI / no chrome.** Suppress the trust preamble (`Program.cs:416`), the
   `Thinking...` spinner (`ConversationManager.cs:268-271` — just `await` the
   first `MoveNextAsync()` with no `Status()` wrapper), and route all status
   writes through a sink that emits plain text (or nothing).

2. **Stop transforming code fences — pass them through verbatim.** In headless
   mode the markdown renderer must **not** convert a fence to a `Rule`. The fence
   lines and body are written exactly as the model produced them, so
   ```` ```json … ``` ```` arrives on stdout byte-for-byte and a consumer can
   grab it by its delimiters. This is the single highest-value change.

3. **Emit tool calls in a stable, parseable form** — one record per call, verb +
   target + result in a fixed shape, instead of the two-line glyph bullets.

### Recommended format — JSONL event stream (phase 2)

One JSON object per line on stdout, `type`-tagged. Proposed schema:

```json
{"type":"tool_call","tool":"bash","input":"weaver traces --route checkout","approved":"auto"}
{"type":"tool_result","tool":"bash","exit_code":0,"output":"...","truncated":false}
{"type":"tool_call","tool":"list_dir","input":".","approved":"auto"}
{"type":"tool_result","tool":"list_dir","entries":42}
{"type":"thinking","text":"..."}
{"type":"assistant_text","text":"Here's what I found ..."}
{"type":"assistant_json","value":{"root_cause":"..."}}
```

Field notes:

- `tool_call.approved` ∈ `auto | prompted | preapproved | rejected`, derived from
  the branch taken in `HandleBashToolCall` (`ConversationManager.cs:1026/1033/1040/1080`)
  and the other tools' approval paths.
- `tool_result` is **per-tool shaped**: `bash` → `exit_code` (+ `truncated`,
  `timed_out`, from `ConversationManager.cs:1221-1231`); `list_dir` → `entries`;
  `grep`/`find_files` → `matches`/`files`; `read_file` → `bytes`/`image`;
  `write_file` → `bytes_written`. Keep a common `output` string where the raw
  text matters, omit it where a count says everything.
- `assistant_json` is the parsed final fence — nb already sees the fence boundary
  in `MarkdownRenderer` (`:76-86`), so it can capture the enclosed block, attempt
  `JsonDocument.Parse`, and emit `assistant_json` on success or fall back to
  `assistant_text` (verbatim, fence included) on failure. This hands the consumer
  the answer already parsed and removes the brace-scanning hack entirely.
- `thinking` is **suppressible** — default on for debugging, `--no-thinking` (or
  `--output=jsonl:quiet`) to drop it.

### Lighter alternative — porcelain text (phase 1, build this first)

For consumers that don't want to pull in a JSON parser, a plain-text mode:

- ANSI off; markdown fences **verbatim** (change #2 above);
- each tool call on one stable, prefixed line and its result on the next:

  ```
  TOOL bash weaver traces --route checkout
  RESULT exit=0
  TOOL list_dir .
  RESULT entries=42
  TOOL write_file /dev/null
  RESULT bytes=0
  ```

- assistant prose printed plain; the final fenced block passed through verbatim
  so `sed -n '/```json/,/```/p'` just works.

`TOOL <name> <input>` / `RESULT <k>=<v>…` is a stable grammar (whitespace-split,
first token is the type, second is the tool), which solves ~90% of the consumer
pain — verbatim fences + parseable tool lines + no color — with a fraction of the
JSONL surface. **Recommendation: ship porcelain first, add JSONL when a consumer
actually needs structured multi-field results.**

### Where to hook it in

The cleanest seam is a small **output sink** abstraction that the interactive
path and the headless paths both implement, rather than threading an `if
(headless)` through ~40 `AnsiConsole.MarkupLine` call sites. Two injection
points carry almost all the value:

- **Tool-call display.** The `• …` / `→ …` / `✓/✗ exit` writes are all inside the
  `Handle*ToolCall` methods and the streaming loop of `ConversationManager.cs`
  (anchors listed above). Route these through one `IToolReporter`-style seam with
  two implementations: the current Spectre writer, and a porcelain/JSONL writer.
  This is where the `tool_call`/`tool_result` records (or `TOOL`/`RESULT` lines)
  are emitted; it's the bulk of the work and it's mechanical.

- **Markdown / final answer.** `MarkdownRenderer` (`Utilities/MarkdownRenderer.cs`)
  needs a passthrough branch: in headless mode, `RenderLine` writes lines
  verbatim (skip the `Rule`/`Markup` transforms at `:76-104`) and the fence
  detector (`:107-111`) is reused to *capture* the final ```` ```json ```` block
  for an `assistant_json`/verbatim emit rather than to *box* it. Smallest possible
  change: a `bool passthrough` on the renderer, set from `OutputMode`.

Rough split: the fence-passthrough is a **flag threaded through the existing
renderer** (small); the tool-call events are a **new sink** (the real work, but
isolated to `ConversationManager`'s `Handle*` methods).

### Backward compatibility

- Interactive TTY behavior is **unchanged** — the mode is opt-in via the flag.
- The color-only auto-gate (`Console.IsOutputRedirected` / `NO_COLOR`) is a
  strict improvement for existing pipe users and changes no structure.
- No change to conversation history, tool semantics, or the wire protocol to
  providers.

## Scope / phasing

- **Phase 0 (trivial):** gate color on `Console.IsOutputRedirected` + `NO_COLOR`.
  Independent, correct always, ships alone.
- **Phase 1 (the 90% cut — recommended first):** `--porcelain` → ANSI off +
  verbatim fences + `TOOL`/`RESULT` lines + suppress trust preamble and spinner.
  Solves the eval harness's parse problem end to end.
- **Phase 2 (full):** `--output=jsonl` event stream with the typed schema above,
  including parsed `assistant_json`. Build when a consumer needs structured
  per-tool result fields rather than lines.

## Key anchors for an implementer

- No `IsOutputRedirected` check anywhere; input-side only at `Program.cs:454, 484`.
- Flag parsing to extend: `Program.cs:212-258`; flag state fields `Program.cs:50-55`.
- Trust preamble to suppress: `Program.cs:416`.
- `Thinking...` spinner to bypass: `ConversationManager.cs:268-271`.
- Streamed prose renderer wiring: `ConversationManager.cs:260, 281, 288`;
  one-shot render at `:307, :816`.
- Code-fence → `Rule` transform to make passthrough:
  `Utilities/MarkdownRenderer.cs:62-104` (fence handling `:76-86`, `IsFence`
  `:107-111`).
- Tool-call display sites to route through a sink: `ConversationManager.cs`
  bash `1028/1035/1046`, exit footer `1236-1238`; read `392/406/420`; find
  `456/462`; grep `496/504`; list_dir `530/537`; todo `579/586`; write
  `1280/1289`; generic `340/685`; fake `606`.
- Theme-color → ANSI mapping (what produces the `38;5;N` codes):
  `Utilities/UIColors.cs:40-59`.
