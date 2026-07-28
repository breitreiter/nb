---
kind: plan
title: Phase 5 — declarative approval policy + bwrap sandbox
created: 2026-07-14
updated: 2026-07-14
status: current
state: active
touches:
  files:
    - ConversationManager.cs
    - Shell/BashTool.cs
    - Shell/TrustSandbox.cs
    - Shell/ApprovalPatterns.cs
    - MCP/McpManager.cs
    - Program.cs
    - ProgramEvaluator.cs
  features: [approval, sandbox, run-specs, headless, trust]
provenance:
  author: claude
---

# Phase 5 — declarative approval policy + bwrap sandbox

## Why this plan exists

Phase 5 of the composable-CLI reorientation (`plans/composable-cli-reorientation.md`).
Two problems, one phase:

1. **Approval logic is scattered and duplicated.** There is no central
   approval-policy object. Each of the ~7 tool-dispatch arms in
   `ConversationManager` open-codes its own precedence chain with subtly
   *different* orderings (bash has a 4-step chain; write/edit/patch key on sandbox
   membership, not `_trustMode`; read-path ignores trust; fetch_url and MCP have
   no bypass at all). The inputs a unified policy must absorb: `_trustMode`,
   `_approvalPatterns` (`--approve`), `McpManager._alwaysAllowTools`
   (`alwaysAllow`), `TrustSandbox.CheckPath`, `CommandClassifier`,
   `SafeCommandPrefixes`, and the `NonInteractive` deny gate.

2. **There is no OS sandbox** (`bugs/shell-tool-no-filesystem-sandbox.md`, High).
   The bash tool runs `bash -c` with no isolation; the C# path/classifier
   heuristics are not a security boundary. Hole #2 leaks arbitrary file contents
   on a *plain* `nb` (no `--trust`) via command substitution inside an
   auto-approved prefix (`echo $(cat /etc/passwd)`). For an untrusted,
   model-driven eval tool this is the load-bearing gap.

This also closes conversation-program tail item #5: the `approval` directive
(deferred from P3.1).

## Ratified decisions (2026-07-14)

- **Scope = unify + directive + bwrap.** Collapse the existing mechanisms into
  one declarative `Approval` policy object, add the `approval` program directive,
  and wire bwrap at the bash spawn point. The `Approval_Enhancements.md` proposal
  (secret-pattern scanning + weighted risk scoring + model self-assessment) is a
  larger, still-unratified layer — deferred, more valuable for interactive coding
  than scripted evals.
- **bwrap requested-but-unavailable → hard-fail the run.** A requested sandbox
  that silently doesn't apply is a false sense of security. bwrap is engaged only
  when explicitly requested (`Sandbox: bwrap`), so hard-fail only affects opt-in
  users; the default (`Sandbox: none`) is unchanged.

## The decision model

Every approval site collapses to one call:

```
enum ApprovalDecision { Allow, Prompt, Deny }
ApprovalDecision Decide(ApprovalRequest req)
```

`ApprovalRequest` carries the tool identity plus the one piece of context that
tool's decision needs: bash → the classified command; file tools → the resolved
path + sandbox check result; fetch → the URL; MCP → the composite tool name.

Each site becomes uniform:

- **Allow** → execute, log `• auto: <tool> …`.
- **Deny** → the existing structured `Error:` tool result (model told not to retry).
- **Prompt** → if `NonInteractive`, treat as Deny (the Phase 0 rule, now
  centralized); else render the site's *existing* interactive prompt (diff
  preview, danger banner). The policy decides; the site still owns prompt UX.

Read-first guards (write/edit require a prior read via `_fileReadTracker`) are
**not** approval and stay put, before the policy call.

## Build order

### P5.1 — the ApprovalPolicy seam (pure refactor, no behavior change)

Introduce `Shell/ApprovalPolicy.cs` (+ `ApprovalDecision`). It owns the two
genuinely *open-coded, duplicated* decision chains and reproduces **today's
behavior exactly**:

- **bash** (`DecideBash`): `--approve` match → safe-allowlist (non-dangerous) →
  trust+sandbox → else Prompt. Returns `Allow` with a reason label (so the
  `• bash (pre-approved)` / `• bash` / `• auto: bash` log lines are preserved) or
  `Prompt`. `SafeCommandPrefixes`/`IsSafeCommand`/`IsBashCommandTrusted` move into
  the policy verbatim.
- **MCP** (`DecideMcp`): `alwaysAllow` → `Allow`, else `Prompt` (wraps
  `McpManager.IsAlwaysAllowed`).

The path tools (read/write/edit/apply_patch) and fetch_url are **not** open-coded
chains — they already share the uniform `TrustSandbox.CheckPath` /
`ApproveReadPath` primitive (in-sandbox → auto, else prompt; fetch always
prompts), so they stay as-is in P5.1 and gain the `Native`/`Default` policy knobs
in P5.2 where those have meaning. The `NonInteractive` → Deny collapse is
unchanged (still the last gate before each interactive prompt).

The policy is constructed inside `ConversationManager` from its existing fields
(`_trustMode`, the `--approve` patterns, `_mcpManager.IsAlwaysAllowed`), so there
is **no constructor-signature or config change** in P5.1 — that arrives in P5.2.
**Verification:** the existing approval evals (`evals/run.sh` non-TTY + `--approve`
sections) and `ApprovalPatternsTests` must stay green unchanged — the proof the
refactor preserved behavior. This is the umbrella plan's "seam insertion, not
decomposition."

### P5.2 — declarative `Approval` config + the `approval` directive

The policy stops hardcoding its rules and reads them from an `Approval` object,
resolved through the Phase 2 config layers and overridable per program:

```jsonc
"Approval": {
  "Bash": ["git status", "ls *"],   // auto-approve matching bash (subsumes --approve)
  "McpTools": ["weather/*"],         // glob server/tool (subsumes alwaysAllow)
  "Native": "sandbox",               // sandbox (default) | all | prompt
  "Sandbox": "none",                 // none (default) | bwrap  (P5.3)
  "Default": "prompt"                 // prompt (default) | deny  — disposition for non-matches
}
```

The **`approval` conversation-program directive** feeds the same policy, parallel
to `mcp`/`tools` (`plans/tool-surface-directives.md`):

```
approval bash "git status"     # add an auto-approve bash pattern
approval mcp weather/*          # add an MCP allow glob
approval native all            # native tools: all | sandbox | prompt
approval sandbox bwrap         # request the bash sandbox (P5.3)
approval default deny          # non-matches deny instead of prompt
```

`--approve <pat>` and `alwaysAllow` become sugar that populate `Approval.Bash` /
`Approval.McpTools`. Wire into `--resolve` (print the effective policy per run)
and `--validate` (bad `Native`/`Default`/`Sandbox` value → exit 1). A headless
run with `Default: deny` and an explicit allow-list is the deterministic
eval-harness posture.

### P5.3 — bwrap sandbox at the bash spawn point

`Approval.Sandbox: bwrap` wraps the bash child at `GetShellCommand`
(`Shell/BashTool.cs:216-220`, feeding `ProcessStartInfo` at 76-90):

```
bwrap --ro-bind / / --dev /dev --proc /proc \
      --tmpfs <mask ~/.ssh ~/.aws ~/.gnupg and other secret dirs> \
      --bind <cwd> <cwd> --chdir <cwd> \
      --unshare-all [--share-net only if the policy opts in] \
      -- bash -c "<command>"
```

- **Availability**: probe for `bwrap` on PATH at policy-resolve time; on
  non-Linux or missing binary with `Sandbox: bwrap` requested → **hard-fail**
  with a clear error (ratified). `Sandbox: none` (default) never probes.
- **Mask set**: cwd + system temp writable; repo-external paths read-only;
  known secret dirs (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/nb`) masked to
  empty tmpfs so even reads see nothing. Kernel-enforced — the only thing that
  holds against arbitrary read commands and command substitution.
- **Network**: default `--unshare-net` (no network from the bash child). The
  policy may opt in (`approval sandbox bwrap-net` or an object field) when a task
  legitimately needs it. Note bwrap gates only the *bash* child; MCP/fetch_url
  run in-process and are governed by their own approval, unaffected.
- Only bash is sandboxed in v1; native file tools already honor the sandbox path
  check. Defense-in-depth classifier tightening (Hole #1/#2 heuristics) is
  explicitly *not* the guarantee and is out of scope — bwrap is the boundary.

## Scope boundaries (v1)

- No secret-detection / risk-scoring engine (`Approval_Enhancements.md`) — later.
- No sandbox for native tools or the nb process itself; bash child only.
- The interactive prompt UX is unchanged; the policy only chooses Allow/Prompt/Deny.

## Verification

- P5.1: existing approval evals + `ApprovalPatternsTests` green, unchanged.
- P5.2: new evals — `approval default deny` refuses an unlisted bash call
  headlessly; `approval bash "…"` auto-approves a match; `approval mcp x/*`
  exposes MCP approval-free; `--resolve`/`--validate` cover the policy.
- P5.3: on a Linux box with bwrap, drive `echo $(cat /etc/passwd)` under
  `Sandbox: bwrap` and confirm the read is masked (empty), and that
  `Sandbox: bwrap` on a box without bwrap hard-fails. A repro fixture mirrors the
  bug's Hole #2.

## Status

Plan written 2026-07-14. Build order P5.1 → P5.2 → P5.3.

- **P5.1 done** (`5d7d9cf`) — `ApprovalPolicy` seam for the bash + MCP chains,
  behavior-preserving.
- **P5.2a done** — the `Approval` config block (`Bash` extends `--approve`;
  `McpTools` allow globs matched against `{server}_{tool}` with `/`→`_`; `Default:
  deny` turns unmatched calls into refusals uniformly at all sites). Policy built
  in `Program.BuildApprovalPolicy`, injected into `ConversationManager`; the
  `DecideMcp`/`DecidePath`/`DecideFetch` decisions now honor `Default`. Note:
  headless already denies unmatched calls (Phase 0), so `Default: deny`'s new
  effect is interactive lockdown + a distinct deny message; the headless-visible
  win is the config-driven allow-lists. Example config + 3 evals + 5 unit tests.
- **P5.2b done** — the `approval` conversation-program directive
  (`approval bash <pat>` / `approval mcp <glob>` / `approval default prompt|deny`),
  parsed by `ProgramParser`, round-tripped through `TranscriptSerializer`
  (`ApprovalEvent`), applied to the policy by `ProgramEvaluator.ApplyApproval` (via
  the `ConversationManager.ApprovalPolicy` accessor + mutators), and covered by
  `--validate` (bad key / default value → exit 1) and `--resolve` (prints
  `approval=<default>(bash:N mcp:N)`). Closes conversation-program tail #5. +4 unit
  tests (parser + serializer round-trip), +3 evals (directive auto-approve / deny /
  validate), README updated. Sandbox key (`approval sandbox`) is intentionally
  absent until P5.3 wires bwrap.
- **P5.3 done** (uncommitted, 2026-07-15) — bwrap sandbox at the bash spawn point.
  `Shell/BwrapSandbox.cs` (`IsAvailable` PATH/OS probe, `TryParse` none|bwrap|bwrap-net,
  `BuildArgs`: `--ro-bind / /` + writable cwd/`/tmp`, secret dirs masked to empty tmpfs,
  `--unshare-all`, net off unless `bwrap-net`). `SandboxMode` + `SetSandbox`/`Sandbox`/
  `SandboxNet` on `ApprovalPolicy`; `BashTool` routes the child through `bwrap` via
  `ProcessStartInfo.ArgumentList` (raw command, sandbox mode read live per call);
  `approval sandbox` directive un-deferred (evaluator + `--validate` keys
  bash|mcp|default|sandbox + value check + `--resolve` prints `sandbox=`);
  `Approval.Sandbox` config; hard-fail-when-unavailable at the config path
  (`RequireSandboxAvailable`→exit 1, inspection modes never probe) and the directive
  path (`SandboxUnavailableException`→exit 1). **Mask scope ratified** (user, 2026-07-15):
  secret dirs only (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/nb`); `/etc/passwd`
  stays readable (world-readable, non-secret) — the acceptance test proves a real
  secret (`~/.config/nb`, the API-key dir) reads empty under the sandbox, superseding
  the plan's literal `/etc/passwd`→empty verify (self-contradictory under `--ro-bind / /`).
  `BwrapSandboxTests` (12) + 2 policy tests; 5 evals
  (`prog-sandbox-{ro,mask,mask-control,badval,resolve}.nb`). Verified live on a
  Linux+bwrap host.
