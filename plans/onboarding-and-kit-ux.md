---
kind: plan
title: Onboarding, diagnostics, and kit UX
created: 2026-06-30
updated: 2026-06-30
status: current
state: exploring
touches:
  files:
    - Program.cs
    - CommandProcessor.cs
    - KitManager.cs
    - ConversationManager.cs
    - McpManager.cs
    - ConfigurationService.cs
  features: [kits, mcp, onboarding]
provenance:
  author: human
---

# Onboarding, diagnostics, and kit UX

## Why this plan exists

A cluster of related gaps make nb hard to set up and easy to
misconfigure, all rooted in one fact: **MCP tools are fully gated behind
active kits** (`ConversationManager.cs:180-184` — tools only surface for
servers in the active-kit set). That single design choice creates several
sharp edges:

- Single-shot mode can never activate a kit, so scripted use gets **zero
  MCP tools, ever**.
- A fresh setup with no active kit silently exposes no MCP tools.
- Servers in `mcp.json` but unreferenced by any kit are invisible with no
  warning.
- None of this is discoverable — there's no way to see what tools are
  actually live.

This plan builds the fixes in priority order. Each item stands alone, but
they share machinery (kit state, config reads) so the sequence matters.

## Current behavior (the constraints we're building against)

- **Kit activation is interactive-only.** Two entry points, both in the
  chat loop: the `+` guard mode in the line editor →
  `HandleKitSelectedAsync` (`Program.cs:542`). Single-shot
  (`ExecuteSingleCommand`, `Program.cs:569`) never touches the kit
  manager.
- **No `--kit` CLI flag.** Arg parsing (`Program.cs:211`) handles only
  `--approve` and `--system`.
- **Active-kit state is in-memory and ephemeral.** `KitManager._activeKits`
  (`KitManager.cs:12`) is a `HashSet` that resets every process. It is
  **not** part of saved history.
- **Kit prompt leaks into persisted history.** `SetKitContext`
  (`ConversationManager.cs:2046`) writes a `[Kit Context]` system message
  into `_conversationHistory`, which *is* saved/loaded
  (`SaveConversationHistoryAsync` 1661 / `LoadConversationHistoryAsync`
  1734). Result: a kit activated interactively leaves its **prompt** in
  history for later single-shot runs while its **MCP servers** stay
  disconnected — the model is told to use tools that aren't wired up.
- **Commands are intercepted in `CommandProcessor.ProcessCommand`**
  (`CommandProcessor.cs:33`) via simple string match, returning a
  `CommandResult`. This runs in both interactive and single-shot
  (`ExecuteSingleCommand` calls it first).
- **Kit MCP connection** is on-demand: `HandleKitSelectedAsync` calls
  `_mcpManager.EnsureServersConnectedAsync(pending)` for the kit's servers
  (`Program.cs:554-566`).

---

## Item 1 — Single-shot kit support (MOST IMMEDIATE) — ✅ IMPLEMENTED

**Status:** Built 2026-06-30. Both layers landed. Shipped:
- Extracted `HandleKitSelectedAsync` → shared `ActivateKitAsync(kitName,
  announce)` (returns bool); interactive `+`, single-shot, and startup
  restore all call it, so prompt + servers always restore together.
- Single-shot parses leading `+kit` tokens (`SplitLeadingKits`): `nb
  +review "x"`, `nb +review +security "x"`, `nb +review` (activate-only,
  no empty prompt sent). An unknown kit reports the error and skips the run.
- Persistence via sidecar `.nb_active_kits.json` in the launch dir
  (gitignored), gated on the history lock owner. Restored quietly at
  startup; saved at exit; deleted when the set is empty.
- `--no-kits` flag clears the persisted set for a directory.
- Additive semantics chosen over "swap": leading tokens add to the
  restored set; `--no-kits` is the reset. (See follow-up note below.)

Verified: 6-case smoke test (Mock provider) + full suite (173 pass).

**Follow-up (built):** `/kit` command added (interactive + single-shot):
`/kit` lists active + available, `/kit drop <name>` deactivates one,
`/kit clear` deactivates all. Re-sets kit context on change and persists
to the sidecar at exit. Pairs with Item 3's `/tools`.

**Goal:** kits reachable from single-shot, ideally persisted across runs
so `nb +kitname "prompt"` works and the kit sticks until changed.

### Design

Two layers; ship them in order so layer 1 delivers value even if layer 2
slips.

**Layer 1 — activation from single-shot.**
- Recognize a leading `+kitname` token in single-shot input. Cleanest
  path: handle it in `CommandProcessor.ProcessCommand` (already called by
  `ExecuteSingleCommand`) — detect input starting with `+`, resolve the
  kit, activate it, connect its servers, set kit context, and either run
  the remaining prompt or just acknowledge if none. This reuses the exact
  logic in `HandleKitSelectedAsync`, so **first extract that body into a
  shared method** (e.g. `KitActivation.ActivateAsync(kitManager,
  mcpManager, conversationManager, kitName)`) and call it from both the
  interactive guard-mode path and the single-shot path.
- Forms to support: `nb +review` (activate, no prompt), `nb +review "look
  at this diff"` (activate + run). Multiple kits: `nb +review +security
  "..."` — the data model already supports multiple active kits, so parse
  all leading `+` tokens.
- A `--kit <name>` flag is the fallback if the `+` parsing is awkward in
  single-shot arg handling; prefer the `+` form for symmetry with
  interactive.

**Layer 2 — persistence across restarts.**
- Persist the active-kit *names* so they survive process exit. Store
  alongside conversation history (same directory-scoped lifetime makes
  sense: kits are project context). Two options:
  - **Sidecar field in the history file** — extend the saved history
    schema with an `activeKits: []` field. Keeps everything in one file,
    but the history format is currently a `JsonElement[]` of messages
    (`LoadConversationHistoryAsync:1742`) — would need a wrapper object.
  - **Separate `.nb_active_kits.json`** — simpler, no history-format
    change, but another dotfile. *Recommended* for a first cut.
- On startup, after `LoadKits` (`Program.cs:423`), read persisted active
  kit names and re-activate them through the **same shared activation
  method** — which reconnects MCP servers AND re-sets kit context. This is
  the fix for the half-state bug: re-activation restores servers and
  prompt **together**.

### The half-state bug — fix as part of this item

Because the `[Kit Context]` message persists in history but servers don't,
naive persistence could double up the prompt (one stale copy from history,
one fresh from re-activation). `SetKitContext` already dedupes on the
`[Kit Context]` marker (replaces in place, `ConversationManager.cs:2050`),
so re-activating through it is safe. **Verify** this on implementation: a
single-shot run after an interactive kit session should end with exactly
one kit-context block matching the persisted active set, and the matching
servers connected.

### Acceptance

- `nb +review "x"` in a clean dir activates review, connects its servers,
  runs the prompt; a subsequent `nb "y"` still has review active with its
  tools live.
- `nb +` with an unknown kit name reports the error and doesn't run.
- No duplicate `[Kit Context]` blocks after re-activation.

---

## Item 2 — Startup health check + no-kit fallback

These two are halves of the same problem; build together.

### Health check

At startup (after MCP config + kits are loaded, `Program.cs:~358-423`),
run a non-fatal config audit and print warnings:

- **MCP servers defined but unreachable:** servers in `mcp.json` not
  referenced by any kit's `mcpServers` in `kits.json`. Compute as
  `mcp servers − union(all kits' mcpServers)`. Warn with the names.
- **`kits.json` missing entirely** while `mcp.json` defines servers — the
  total-lockout case. Stronger warning + pointer to fix.
- Keep it cheap and pure (no network); just config cross-referencing.
- Make it suppressible (e.g. a `"SuppressHealthCheck": true` setting) so
  power users aren't nagged.

### No-kit fallback

Decide the semantics first — this changes what the health check should
even warn about:

- **Option A — implicit "expose all servers" when no kit is active.**
  `GetActiveMcpServers` returns all configured servers when `_activeKits`
  is empty. Makes MCP work out-of-the-box; weakens the "servers silently
  off" warning into "servers silently on."
- **Option B — a configurable default kit** (`"DefaultKit": "+review"`)
  activated at startup when nothing else is active. More explicit, keeps
  gating meaningful.

**Recommendation:** Option B as the headline feature (explicit, composes
with Item 1's persistence), with Option A available as a special
`DefaultKit: "*"` value meaning "all servers, no prompt." Resolve this
choice before coding the health check, since the warning text depends on
it.

### Acceptance

- Fresh install with servers in `mcp.json`, no `kits.json`: clear startup
  warning explaining tools are gated and how to fix.
- With a default kit set: MCP tools available immediately in both modes
  without manual activation.

---

## Item 3 — `/tools` command — ✅ IMPLEMENTED

**Status:** Built 2026-06-30. Shipped:
- `ConversationManager.GetAvailableTools()` returns `ToolDescriptor`
  (group, name, approval), mirroring the assembly in
  `SendMessageInternalAsync` (cross-referenced with a sync comment). It's
  the single source of truth — reads the real tool instances so names
  match exactly what the model sees.
- `/tools` handler in `Program.cs` renders a grouped Spectre table with an
  approval legend, wired into interactive + single-shot. Added to the
  completion menu.
- Approval tokens reflect current flags: `auto (cwd)` read-only,
  `auto (trust)` writes/bash under `--trust`, `auto (always-allow)` MCP
  allowlist, `prompt` otherwise. Verified the `--trust` flip live.
- Diagnostic line when no MCP tools are active, pointing at `+kit`/`/kit`.

Verified: native/trust/nobash renders + a live MCP kit (built-in-tester).
Full suite green (173).

**Bug found + fixed here:** `alwaysAllow` in mcp.json was dead for
kit-gated tools. The allow-list was keyed `{server}_{tool}` but the LLM
and the approval check (`ConversationManager.cs:185,623`) use the **bare**
tool name from `GetToolsForServers` — they never matched, so allow-listed
MCP tools always prompted. Fixed in `McpManager.cs`: the allow-list is now
keyed by the actual tool name, and config entries match leniently
(`-`↔`_`, case-insensitive) so `"current-time"` matches the tool exposed
as `current_time`. Verified: `echo` and `current_time` now report
`auto (always-allow)` in `/tools` and skip the approval prompt at runtime.
Note this is a behavior change — allow-listed MCP tools now actually
auto-approve.

**Goal:** show all currently-available tools grouped by source, with
auto-approve status.

### Design

- Add `/tools` to `CommandProcessor.ProcessCommand`. Render a grouped
  Spectre table/tree. Works in interactive; in single-shot it prints and
  exits (already supported via `ExecuteSingleCommand`).
- **Sources to enumerate:**
  - *Native tools* — bash, read_file, write_file, edit_file, find_files,
    grep, list_dir, set_cwd, todo. Conditioned on what's actually enabled
    (e.g. `--nobash` disables tools; see existing tool-registration in
    `ConversationManager.cs:~193-210`).
  - *MCP tools* — per connected server (`McpManager.GetToolsForServers` /
    `GetConnectedServerNames`). Only currently-active-kit servers will be
    live — which makes this command *also* a diagnostic for Items 1-2.
  - *Resource tools* — when MCP servers active
    (`ConversationManager.cs:187`).
- **Auto-approve column** — surface the three independent mechanisms:
  - Native read-only tools (read_file, grep, find_files, list_dir) always
    auto-run.
  - Trust mode (`--trust` / `"Trust": true`) auto-approves write/edit and
    non-dangerous in-sandbox bash (`Shell/TrustSandbox.cs`).
  - Per-server `alwaysAllow` lists in `mcp.json` (e.g. built-in-tester
    allows `echo`, `current-time`).
  - Show effective status per tool given current flags, not just the
    static capability.

### Acceptance

- `/tools` lists every live tool under its source with a clear
  auto-approve indicator; running with `--trust` vs without visibly
  changes the column.

---

## Item 4 — Self-customization kit

**Goal:** a kit (no MCP servers — pure prompt) that teaches nb to edit its
own config so the user can say "add an Anthropic provider" or "make a kit
for X".

### Design

- Ship a `+config` (or `+setup`) kit in the default `kits.json` /
  `kits.example.json`, prompt sourced from a file via the existing
  `PromptFile` mechanism (`KitManager.cs:37-44`) — keeps the guidance in a
  readable `.md`, not a JSON string.
- Content the prompt must cover (this is where the config knowledge
  lives):
  - `appsettings.json` provider schema — `ActiveProvider` + `ChatProviders`
    array, per-provider fields, `EditToolStyle`, the
    appsettings/appsettings.example sync rule.
  - `mcp.json` server entries (stdio vs http, `alwaysAllow`).
  - `kits.json` kit definitions (`prompt`/`promptFile`/`mcpServers`).
  - **The gating relationship** — that MCP servers need a kit reference to
    be reachable. This is the highest-value fact to teach.
- Reuses Item 1's machinery only incidentally; mostly a content task. Low
  code risk.

### Acceptance

- `nb +config "add an Anthropic provider with my key"` produces a correct
  edit to `appsettings.json` (and keeps the example file in sync).

---

## Item 5 — First-run experience (LEAST PRESSING)

**Goal:** on a fresh setup (no provider configured / no usable
`appsettings.json`), walk the user through configuring a first provider,
then point at the other customizable files.

### Design

- Detect "fresh" cheaply: `ConfigurationService` finds no configured
  providers (`GetConfiguredProviders` empty) or no `appsettings.json`.
- Interactive wizard (Spectre prompts): pick a provider type, enter
  endpoint/key/model, write a minimal valid `appsettings.json`.
- After provider setup, print a short "what's next" pointing at `mcp.json`
  (add tool servers), `kits.json` (group context + servers), and mention
  the `+config` kit from Item 4 as the assisted path.
- Only triggers in interactive mode with no usable config; single-shot
  with no config should error clearly (and *suggest* running `nb` with no
  args to set up), not launch a wizard into a pipe.
- Complements Item 2: first-run sets things up; the health check catches
  later drift.

### Acceptance

- Deleting `appsettings.json` and running `nb` launches the wizard and
  ends with a working provider; piping into `nb` with no config errors
  cleanly instead of hanging on a prompt.

---

## Build sequence & shared work

1. **Item 1** first — and within it, the **extract-shared-activation**
   refactor is the keystone the rest leans on (re-activation on startup,
   single-shot, and default-kit all call it).
2. **Item 2** — depends on nothing from Item 1 technically, but the
   default-kit fallback naturally reuses the shared activation method, and
   the half-state fix from Item 1 should land first.
3. **Item 3** — independent; good to land early as it doubles as a
   diagnostic for verifying 1 & 2.
4. **Item 4** — mostly content; low risk; any time.
5. **Item 5** — last; largest UX surface, least urgent.

### Open decisions (resolve before coding the dependent item)

- **Item 1:** persistence location — sidecar `.nb_active_kits.json`
  (recommended) vs history-file schema change.
- **Item 2:** no-kit semantics — default kit (recommended) vs implicit
  expose-all. Gates the health-check warning text.
- **Cross-cutting:** is per-directory the right scope for persisted active
  kits? (Matches history scope; assume yes unless a global default is
  wanted.)
