---
kind: plan
title: Tool dialects — matching a model's trained tool surface
created: 2026-08-11
updated: 2026-08-14
status: superseded
state: shelved
superseded-by: plans/harness-emulation.md
touches:
  files:
    - ConversationManager.cs
    - Facade/NbRuntime.cs
    - Shell/EditFileTool.cs
    - Shell/ReadFileTool.cs
    - Shell/WriteFileTool.cs
    - Transcript/TranscriptPorcelainWriter.cs
  features: [tool-surface, provider-config, transcript]
provenance:
  author: claude
---

# Tool dialects — matching a model's trained tool surface

> **Superseded 2026-08-14 by `plans/harness-emulation.md`.** The rename-table mechanism
> here cannot express the structurally different schemas (`MultiEdit`, enum-valued
> `output_mode`) or the result formatting that harness emulation needs, and the
> selection axis is wrong — the choice belongs to the experiment, not the provider
> entry. `QwenCodeHarness` covers this plan's committed scope. Kept for the qwen-code
> mapping table below, which is still accurate and feeds that costume directly.

## Context

Qwen3-Coder ("qwen coder next", the `qcoder` profile on imp) behaves badly at file edits
under nb. The likely cause is that nb advertises a tool surface that differs — in both tool
names and parameter names — from the surface Qwen was RL-trained against, which is the
`qwen-code` harness (a Gemini-CLI fork).

Confirmed from `QwenLM/qwen-code` source (`packages/core/src/tools/tool-names.ts` plus each
tool's schema literal):

| nb (canonical) | qwen-code (trained) |
|---|---|
| `edit_file(path, old_string, new_string, replace_all)` | `edit(file_path, old_string, new_string, replace_all)` |
| `write_file(path, content)` | `write_file(file_path, content)` |
| `read_file(path, offset, limit)` | `read_file(file_path, offset, limit)` |
| `find_files(pattern, path, max_results)` | `glob(pattern, path)` |
| `grep(pattern, path, file_pattern, case_insensitive, max_results, output_mode)` | `grep_search(pattern, path, glob, limit)` |
| `list_dir(path)` | `list_directory(path, ignore, file_filtering_options)` |
| `bash(description, command, timeout_seconds)` | `run_shell_command(command, description, directory, is_background, timeout)` |
| `fetch_url` | `web_fetch` |

The sharpest mismatch sits exactly where the symptom is: `path` vs `file_path` on the three
file tools. A model emitting `file_path` against nb's schema produces a call nb reads as an
empty path — which presents as the model "being dumb" rather than as a schema mismatch.

This is a moving target: qwen-code named its edit tool `replace` through v0.0.5 and `edit`
today. That argues for a *named, data-driven mapping* rather than renaming nb's own tools to
chase one harness.

nb already has a precedent for per-provider tool-surface variation — `EditToolStyle`
(`nb.Core/Facade/NbRuntime.cs:68-76`) swaps `edit_file`+`write_file` for `apply_patch` for
GPT-family models. But that works by choosing *which tool object gets constructed*; it cannot
rename or re-signature an existing tool. This plan adds the missing capability.

## Step 0 — cheap diagnosis before building anything

Qwen3-Coder emits tool calls as XML (`<tool_call><function=name><parameter=k>v</parameter>…`),
not JSON, and there is a known serving-side failure where the model omits the opening
`<tool_call>` tag under the stock chat template
([QwenLM/Qwen3-Coder#475](https://github.com/QwenLM/Qwen3-Coder/issues/475)). That produces
identical-looking "dumb about edits" behavior and is a llama.cpp/template problem, not an nb
problem.

Run one edit-heavy program against `qcoder` with `--output jsonl` and inspect whether tool
calls arrive well-formed and which argument keys the model chose. If they arrive as
`file_path`, this plan is the fix. If they arrive malformed or not at all, fix the chat
template on imp first and stop here.

## Decisions

- **Descriptions stay nb's.** The dialect remaps names and parameters only. Qwen was also
  tuned on qwen-code's description prose, but vendoring that prose means two sources of truth
  for tool behavior. Not worth it.
- **Selection is per-provider-entry config only.** A `"ToolDialect": "qwen"` field on the
  `ChatProviders` entry, beside `EditToolStyle`. No CLI flag, no program directive, no
  inference from the model slug.
- **Transcripts record wire names as sent**, so the transcript is a faithful record for
  debugging the dialect itself.

## Scope

**Committed scope is the three file tools.** That is where the reported failure is, and it is
the smallest change that exercises the mechanism end to end.

The mechanism cost is fixed and paid once: one new file, two hook points. After that, adding
`glob` / `grep_search` / `list_directory` / `run_shell_command` is **one row of data each**,
no new code. Those rows are deliberately *not* in this plan — decide whether they are worth it
after the file trio is measured against a real qwen run.

## Design

Two choke points, because nb's tool plumbing already has exactly two:

1. **Outbound (registration).** Native tools are declared with
   `AIFunctionFactory.Create(lambda, name:, description:)`, so the C# lambda parameter names
   *are* the wire JSON property names (`nb.Core/Shell/EditFileTool.cs:16-34` and siblings).
2. **Inbound (dispatch).** `ConversationManager` does not auto-invoke; it hand-dispatches on
   `functionCall.Name` and re-reads arguments by literal string key
   (`nb.Core/ConversationManager.cs:566-800`). The delegates handed to `AIFunctionFactory` are
   near-inert — `ApplyPatchTool`'s is literally `(string input) => input`.

So a dialect is: rewrite on the way out, normalize on the way in. Everything downstream —
approval, `TrustSandbox`, `FileReadTracker`, the `tools` directive vocabulary — keeps seeing
canonical names and never learns dialects exist.

### New file: `nb.Core/Shell/ToolDialect.cs`

- A `ToolDialect` record: a name plus a map
  `canonicalToolName → (wireToolName, paramRenames, hiddenParams)`.
- A static registry — `"default"` (identity) and `"qwen"`. Case-insensitive lookup; an
  unrecognized value falls back to identity **with a warning** (unlike `EditToolStyle`, which
  silently swallows typos — don't repeat that).
- `AIFunction Apply(AIFunction inner)` returns the original instance when identity, otherwise
  a small `AIFunction` decorator overriding `Name` and `JsonSchema`. The schema rewrite is
  `JsonNode` surgery on the reflected schema: rename keys under `properties`, rewrite the
  `required` array, drop `hiddenParams`. `Description` and `InvokeCoreAsync` delegate to the
  inner function unchanged.
- `ToCanonical(wireName, args) → (canonicalName, canonicalArgs)` for the inbound direction:
  reverse both maps, leave unknown keys untouched.

The `qwen` table is three rows:

```
read_file  → read_file  { path → file_path }
write_file → write_file { path → file_path }
edit_file  → edit       { path → file_path }
```

### Wiring

- **`Facade/NbRuntime.cs:68-76`** — read `providerConfig?["ToolDialect"]` inside the existing
  provider-entry lookup that already reads `EditToolStyle` (do not add a second lookup),
  resolve it, and pass it into the `ConversationManager` constructor (`NbRuntime.cs:111-116`)
  as a new optional parameter defaulting to identity — so the library surface and all existing
  tests keep compiling untouched.
- **`ConversationManager.cs:344-397`** — wrap each `CreateTool()` result in
  `_dialect.Apply(...)`. The `AllowsNative("edit_file")` gates stay canonical.
- **`ConversationManager.cs:2143-2189`** — `GetAvailableTools()` is a hand-maintained mirror of
  the assembly block (the comments at `:327` and `:2141` say to keep them in sync). Apply the
  dialect there too, or it disagrees with what was actually advertised.
- **`ConversationManager.cs:~519`** — at the top of the tool-call loop, *after* the existing
  membership gate (which must keep matching against the wire names now in
  `requestOptions.Tools`), capture `var wireName = functionCall.Name` and replace
  `functionCall` with a canonicalized copy. Every dispatch arm below then works unchanged.
- **Transcript** — the ~11 `LogToolCall(functionCall.Name, …)` calls in the dispatch arms pass
  `wireName` instead, per the decision to record what was sent.

### Consequences worth knowing

- A program says `tools -edit_file` (canonical) while its transcript shows `edit` (wire). That
  asymmetry follows directly from the two decisions above; it is intentional and belongs in
  the docs.
- `TranscriptPorcelainWriter.cs:25` keys `PrimaryArgKeys` on *argument* names
  (`"command", "path", "pattern", "url", "query", "input"`). Under the qwen dialect the logged
  key becomes `file_path`, so porcelain output would silently degrade to compact JSON. Add
  `"file_path"` to that set.
- `EditToolStyle` and `ToolDialect` are independent and compose: `ApplyPatch` + a dialect that
  doesn't mention `apply_patch` is a no-op on that tool.

## Files touched

New: `nb.Core/Shell/ToolDialect.cs`, `nb.Tests/ToolDialectTests.cs`.
Modified: `nb.Core/Facade/NbRuntime.cs`, `nb.Core/ConversationManager.cs`,
`nb.Core/Transcript/TranscriptPorcelainWriter.cs`, `appsettings.example.json`, `CLAUDE.md`
(Configuration Schema, beside `EditToolStyle`), `README.md`.

## Verification

1. **Unit** (`nb.Tests/ToolDialectTests.cs`): identity dialect returns the same `AIFunction`
   instance; the qwen dialect renames `edit_file`→`edit` and rewrites `properties`/`required`
   in the emitted schema; `ToCanonical` round-trips name and args; an unknown dialect name
   falls back to identity and warns.
2. **Wire-level, no model needed** — the real end-to-end check. Put `ToolDialect` on a Mock
   provider entry and run `echo 'run MOCK:…' | ./nb - --output jsonl` from `bin/Debug/net10.0`.
   Assert the advertised schema carries `edit`/`file_path`, that a scripted `edit` +
   `file_path` call dispatches and actually edits the file, and that the transcript records
   the wire name. `Providers/Mock/MockProvider.cs:139` synthesizes args by tool name and needs
   a case for the dialect tool.
3. **Regression**: `dotnet test` — `ToolSurfaceTests`, `TranscriptSerializerTests`,
   `TranscriptMapperTests`, `TranscriptPorcelainWriterTests` all hardcode canonical names and
   must stay green with no dialect configured.
4. **Live**: `ssh imp '~/.local/bin/swap-model qcoder'`, then run the same edit-heavy program
   with and without `"ToolDialect": "qwen"` and compare edit success rates. This measurement
   decides whether the file trio was enough and whether to extend the table.

## Open questions

- Is a dialect the right primitive, or is this really "nb's canonical names are idiosyncratic
  and should move toward the de-facto standard"? `read_file`/`write_file` already match;
  `edit_file`/`find_files`/`list_dir`/`bash` are nb-specific coinages. Renaming nb's canon
  instead would fix Qwen *and* likely help other models, at the cost of churning the `tools`
  directive vocabulary, docs, and every transcript fixture.
- Does the mismatch actually explain the observed behavior? Step 0 answers this, and it should
  be answered before any of the above is built.
