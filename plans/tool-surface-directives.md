---
kind: plan
title: Wire the mcp/tools directive effects — the tool surface as first-class config
created: 2026-07-13
updated: 2026-07-13
status: current
state: active
touches:
  files:
    - ProgramEvaluator.cs
    - ConversationManager.cs
    - Program.cs
    - MCP/McpManager.cs
  features: [run-specs, tool-surface, preset-floor, headless]
provenance:
  author: claude
---

# Wire the mcp/tools directive effects

## Why this plan exists

`McpEvent`/`ToolsEvent` parse, round-trip through the serializer, and print in
`--resolve` — but `ProgramEvaluator` has an explicit no-op for them
(`ProgramEvaluator.cs:55-57`), so the tool surface the model actually sees is
never reshaped. This is the one directive that is *inspectable but inert*, which
undercuts the "specs are the sole tool-surface mechanism" thesis the kit removal
committed to (`plans/conversation-program-evaluator.md`, 2026-07-12 revision).

This is Phase 3 tail item #2 from the conversation-program reorientation. It
closes the gap between what `--resolve` promises and what a run does.

## The surface today

Assembled fresh per turn in `ConversationManager` (`:204-268`), with no filter:

- **Native tools** — the `tools` vocabulary (canonical `AIFunction` names):
  `bash`, `read_file`, `write_file`, `edit_file`, `list_dir`, `find_files`,
  `grep`, `fetch_url`, `apply_patch`. Each is constructor-injected and already
  nullable via `--nobash` etc. `todo_write`/`todo_read` and the MCP resource
  tools are "always registered."
- **MCP tools** — server-prefixed (`tester_current_time`). `mcp` operates on
  **server names**; `McpManager.GetToolsForServers(names)` already exists (a
  kit-era leftover) to filter by server.

## Decisions (ratified 2026-07-13)

**MCP baseline is strict-empty.** A program/spec with no `mcp` directive exposes
no MCP tools; it opts servers in with `mcp +tester`. This matches the schema
comment ("baseline empty") literally.

Consequence — and the part that *improves* the design: the bare/`-p`/single-shot
path can no longer lean on implicit "expose all connected" behavior in
`ConversationManager`. Instead the **default preset carries an explicit
`McpEvent`** enumerating the connected servers, built from
`_mcpManager.GetConnectedServerNames()` at preset-build time (which runs after
`ConnectAllAsync`). MCP exposure becomes a first-class, `--resolve`-visible
directive rather than buried engine behavior — dead-on with the preset-floor
model (`rules/preset-floor.md`). `--spec chat` returns the same preset, so it
too carries the connected-servers directive; correct, since `chat` == the full
bare-path envelope.

Native `tools` baseline stays all-on everywhere; deltas filter. No preset
directive needed because baseline == current behavior, so the bare path is
byte-identical to today and there is zero regression on the existing MCP eval
(a bare single-shot: `nb --mcp manifest.json … "trigger"`).

**`--validate` stays parse-only.** A delta against an absent name is a harmless
runtime no-op (`tools -bahs` matches nothing to remove; `mcp +notaserver` adds
an allow-list entry `GetToolsForServers` never matches), so name-checking earns
no code in v1.

## The mechanism

A small record threaded through one setter, mirroring how a provider change
flows through `SwitchProvider`.

```
ToolSurface {
    IReadOnlySet<string>? NativeAllow;   // null => all native on; a set => only these
    IReadOnlyList<string>? McpServers;   // null => MCP uncontrolled (all); a list => allow-list (may be empty)
}
```

1. **Shared fold.** The delta-folding already lives in `--resolve`
   (`Program.cs:894-917`). Extract it into one resolver that folds a run's
   preceding `SurfaceDirectiveEvent`s into a `ToolSurface`, and have *both*
   `--resolve` and the evaluator consume it. Single source of truth: what
   `--resolve` prints is provably what runs.
   - Native fold: start from "all," apply `Reset` (→ empty set), `Remove`, `Add`.
     Track as an allow-set only once the surface is constrained; the all-on
     baseline is represented by `NativeAllow == null`.
   - MCP fold: start from `McpServers == null` (uncontrolled). The first `mcp`
     directive switches to an allow-list (`null` → empty list, then apply
     deltas). Under strict-empty a program's first directive is what constrains
     it; the bare path is constrained by the preset's enumerated `McpEvent`.

2. **Evaluator.** Track the resolved `ToolSurface`; before each `RunEvent`, call
   `_conversation.SetToolSurface(surface)`. Replace the `McpEvent or ToolsEvent`
   no-op with the fold.

3. **ConversationManager.** Store the surface (default: all-native, MCP
   uncontrolled — i.e. today's behavior). In the per-turn assembly:
   - Native: gate each `mcpTools.Add(...)` with
     `surface.AllowsNative("bash")` etc. `todo_*` and resource tools stay
     unconditional (internal bookkeeping, out of the v1 vocabulary).
   - MCP: `surface.McpServers is null ? _mcpManager.GetTools()
     : _mcpManager.GetToolsForServers(surface.McpServers)`.

4. **Default preset.** `BuildDefaultPresetEvents` appends
   `new McpEvent { Add = connectedServerNames }` after the persona `SystemEvent`,
   so the bare/`-p`/single-shot path exposes exactly the connected servers.

## Scope boundaries (v1)

- Filtering only — no per-run *approval* policy (Phase 5's `approval` directive).
- `todo_*` and resource tools are always-on, not in the `tools` vocabulary.
- No wildcard `mcp all` token; the preset enumerates connected servers instead.
  A wildcard is a possible follow-up if servers churn within a run.

## Verification

- Evals (MockProvider, `MOCK:tool=<name>` scripts one tool call):
  - `tools none` + `MOCK:tool=bash` → dispatch misses → `tool_error` in jsonl.
  - `tools -bash` → same for bash; a non-removed tool still dispatches.
  - `mcp +tester` in a program → the tester tool dispatches; a bare program
    (no `mcp`) → not exposed.
  - The existing "mcp: manifest tool dispatches" bare-path eval stays green
    (proves the preset's enumerated `McpEvent` preserves exposure).
- `dotnet test` + `bash evals/run.sh` green; 0 warnings.

## Status

**Implemented 2026-07-13.** Unit 251 (+`ToolSurfaceTests`), evals 40 (+ a
tool-surface section), 0 warnings.

### Revisions (2026-07-13, during implementation)

Two things changed from the plan above once the code met reality:

1. **The default preset does NOT carry an `McpEvent`.** The plan proposed
   enumerating connected servers into `BuildDefaultPresetEvents`. Unnecessary: the
   bare/-p path's preset has no `RunEvent`, so it never calls `SetToolSurface` —
   the actual run keeps `ConversationManager`'s default `ToolSurface.All` (null
   MCP = all connected), which already reproduces today's behavior. So the split
   is cleaner than planned: `ToolSurface.Fold` (programs) is **strict-empty**;
   `ToolSurface.All` (bare path, never folded) is uncontrolled/all. Consequence:
   `--spec chat` reproduces the persona floor but not the MCP surface — the
   program path is *always* strict-empty for MCP, `chat` included. Documented in
   `rules/preset-floor.md` and the README.

2. **The program path now calls `ConnectAllAsync`.** Previously the `--program`
   branch returned before connecting MCP, so `mcp +server` had nothing to select.
   It connects after the `--validate`/`--resolve` short-circuits (inspection stays
   connection-free).

3. **Bug found + fixed in `McpManager`:** `_toolsByServer` stored *unprefixed*
   tools while the model calls (and the surface advertises) the `{server}_{tool}`
   composite. `GetToolsForServers` therefore advertised names the model never
   uses, so `mcp +server` silently exposed nothing. Now both `_toolsByServer` and
   `_mcpTools` hold the same prefixed instances.

### How filtering is enforced

The dispatch table in `ConversationManager` still wires every constructed tool
regardless of the surface, so filtering is enforced by a single **membership
gate** at the top of tool dispatch: a call whose name is not in the advertised
`requestOptions.Tools` is refused ("Tool 'x' not found") instead of executed.
This makes the surface real even when a (mock or misbehaving) model calls a tool
that was never advertised.
