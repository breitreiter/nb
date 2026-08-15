# The documented `approval_denied` exit code can never be produced

Status: Open (2026-08-15) — found during the codebase hygiene sweep
(`TODO.md`, "Run a ReSharper pass"), while checking an open question in
`plans/approval-without-prompts.md`.

## Symptom

Two published specs promise exit code `4` / `exit_reason: approval_denied`:

- `docs/conversation-program-cli.md:86` — *"`4` | `approval_denied` — a tool needed
  approval and policy denied it."*
- `docs/conversation-program-api.md:263` — same row, in the doc owed to the downstream
  consumer.

No run can produce either. A program whose tool call is denied by the approval policy
exits `0` with `exit_reason: "ok"`.

## Reproduction

```bash
cd bin/Debug/net10.0
printf 'approval default deny\nrun MOCK:tool=bash echo hello\n' \
  | ./nb --config ../../../evals/test-appsettings.json - --output jsonl
```

Observed (trimmed):

```json
{"type":"tool_result","turn":1,"id":"mock-call-1","output":"Error: bash (Run) was denied by the approval policy (default: deny) and no allow-rule matched. Permission denied — do not retry; try a different approach.\n\n[nb] bash has failed 1 time(s); 2 attempt(s) left before this turn is aborted..."}
{"type":"result","turn":null,"exit_reason":"ok","usage":{...},"turns":2,"tool_calls":1}
```

Exit code: `0`. Expected per the docs: `4` / `approval_denied`.

## Cause

`ExitReasons.ApprovalDenied` (`nb.Core/Transcript/ExitReasons.cs:25`) is declared and
mapped to exit `4` (`:32`), but **nothing ever assigns it**. Every producer in the
codebase sets one of `Ok`, `MaxToolCalls`, `ProviderError`, `RateLimited`, `TimeBudget`,
`TokenBudget` or `ToolErrorLimit`:

```
$ grep -rn "ExitReasons\.[A-Z]" --include=*.cs . | grep -v ExitReasons.cs
# → no ApprovalDenied assignment anywhere, including tests and evals
```

A denial is built as an ordinary `ToolOutcome.Fail` (`NbHarness.DenyByPolicy` /
`DenyNonInteractive`), so it enters the normal tool-result path and carries no signal
that it was a *policy* refusal rather than a tool that went wrong.

## Consequences

1. **A published contract is unsatisfiable.** A consumer branching on exit `4` — the API
   doc is written for exactly such a consumer — has dead code, and reads a denied run as
   a clean success.
2. **A fully-denied run reports `ok`.** The run above did nothing the program asked for
   and exited `0`. The transcript records the denial, but the exit code and `exit_reason`
   both say the run succeeded.
3. **Denials are accounted as tool failures.** Note the second half of the tool result:
   *"bash has failed 1 time(s); 2 attempt(s) left before this turn is aborted."* Enough
   denials abort the turn as `tool_error_limit` (exit `3`). So the observable exit code
   for a denied run is `0` or `3` depending only on how many times the model retried
   something it was told not to retry — neither of which is the documented outcome, and
   the `3` case actively misattributes a policy decision to a malfunctioning tool.

## Fix options

- **Implement it.** Track that a denial occurred; a run whose terminal failure is a
  denial ends `approval_denied` / `4`. Needs a rule for denied-but-recovered — recommend
  `ok`, with the denial visible in the `result` trailer (see
  `plans/approval-is-not-a-boundary.md` sequencing item 0, which adds denial counts).
  Probably also wants denials excluded from the tool-error budget, or counted in a
  separate one, per consequence 3.
- **Retract it.** Delete the constant and both doc rows; denials ride `tool_error_limit`
  like any other tool failure. Cheaper, and loses the distinction between "the model was
  not authorized" and "the model kept failing at its task".

Recommend implementing. `plans/approval-without-prompts.md` makes denial the *only*
non-allow outcome, which makes that distinction more load-bearing rather than less.

## Notes

The `4` row predates the harness work and is not a regression from it — the constant
appears to have been specified alongside the exit-code table and never wired up.
`bugs/Approval_Search_Directive_Not_Implemented.md` is the same shape (approval surface
specified ahead of implementation) and the two may want fixing together.
