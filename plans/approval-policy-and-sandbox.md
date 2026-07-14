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

Introduce `Shell/ApprovalPolicy.cs` (+ `ApprovalRequest`/`ApprovalDecision`). It
absorbs the scattered inputs and reproduces **today's behavior exactly**:

- bash: `--approve` match → safe-allowlist (non-dangerous) → trust+sandbox →
  else Prompt (dangerous defaults to a No-default prompt).
- write/edit/apply_patch: sandbox membership (via `CheckPath`, not `_trustMode`)
  → else Prompt.
- read-path: sandbox membership → else Prompt.
- fetch_url: always Prompt.
- MCP: `alwaysAllow` → else Prompt.

Consult it at all 7 sites, deleting the open-coded chains. The `NonInteractive`
→ Deny collapse moves into the Prompt-handling helper. **Verification:** the
existing approval evals (`evals/run.sh` non-TTY + `--approve` sections) and
`ApprovalPatternsTests` must stay green with zero changes — that is the proof the
refactor preserved behavior. This is the umbrella plan's mandated "seam
insertion, not decomposition."

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

Not started (plan written 2026-07-14). Build order P5.1 → P5.2 → P5.3, each its
own commit(s). P5.1 is a behavior-preserving refactor gated by the existing
suite; the new surface arrives in P5.2–P5.3.
