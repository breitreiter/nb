---
kind: plan
title: Approval without prompts — refusals the model can read
created: 2026-08-15
updated: 2026-08-15
status: current
state: proposed
touches:
  files:
    - nb.Core/Harness/NbHarness.cs
    - nb.Core/Shell/ApprovalPolicy.cs
    - nb.Core/ConversationManager.cs
    - docs/conversation-program-cli.md
    - CLAUDE.md
    - README.md
  features: [approval, headless, trust, tool-surface, transcript]
provenance:
  author: claude
  source: codebase-hygiene sweep, 2026-08-15 (TODO.md "Run a ReSharper pass")
relations:
  component-of: plans/approval-is-not-a-boundary.md
  supersedes-in-part: plans/approval-policy-and-sandbox.md
---

# Approval without prompts — refusals the model can read

## Why this plan exists

`NbHarness` contains seven hand-rolled copies of the same TTY interaction: flush
`Console.KeyAvailable`, loop on `ReadKey`, match `y/Y/n/N/\r/\n`, sometimes handle `?`,
sometimes collect a rejection reason. They are the last chat-client furniture in the tool
layer.

This plan deletes them. What replaces them is not a smaller prompt — it is a **refusal
written for the model to read and act on**, plus a console line written for the human to
act on.

The prompts were found during a hygiene sweep as a duplication finding. They are in a
plan instead of a cleanup commit because removing them changes behaviour, changes a
documented promise, and needs tests that do not exist.

## The design already moved; the code did not

This is not a new proposal. It is the code catching up with three decisions already on
paper:

1. **The CLI doc already specifies it.** `docs/conversation-program-cli.md:90` — an
   unmatched call "is **denied** rather than prompted — grant what a run needs with
   `approval`". Line 455 lists "Headless + unmatched approval → denied (**never hangs**)"
   as a design property. Exit code `4` (`approval_denied`) is specified for the outcome —
   though only specified; see open question 4.
2. **`plans/approval-is-not-a-boundary.md` already made the argument.** Its Part 1: every
   approval rule "assumed a human in the loop who is no longer there, and the rules
   survived the identity that justified them." The prompt loop *is* that human, still
   being waited for.
3. **The code says so in passing.** `ApprovalPolicy.DecideSearch`'s comment reasons from
   "nb's primary diagnostic mode is non-interactive."

## The incoherence, precisely

`NbHarness.NonInteractive => Console.IsInputRedirected`. The same program produces
different behaviour depending on whether stdin is a pipe:

```
$ echo 'run …' | nb -          # unmatched bash call → denied, exit 4
$ nb program.nb                # unmatched bash call → blocks on a keypress, may execute
```

For a tool whose product is a transcript you compare across harness costumes, tool
authorization resolving through an unrecorded keypress is not a variable you want. It is
the same objection `HarnessRegistry` already makes when it hard-errors on an unknown
harness name rather than falling back: comparative numbers that mean nothing are worse
than a missing run. A run where someone pressed `y` and a run where they pressed `n` are
different experiments, and nothing in the transcript distinguishes them.

Three supporting facts:

- **Removal cannot make anything more permissive.** The prompt is the *only* path by
  which a call nothing allow-listed can execute. Every other route (`--approve`, the
  routine-command list, `--trust` + sandbox, `approval` directives) is a pre-authorization
  recorded in the program or the flags.
- **It is the untested code.** Nothing in `nb.Tests` references the prompt loop or the
  `NonInteractive` gate. The 529 green tests exercise `ApprovalPolicy`'s *decisions*.
- **It is the concurrency hazard.** Seven blocking reads against the global console, in
  the subsystem `bugs/Concurrent_Runs_Collide_On_The_Global_Console.md` is already about.

## Worked example

A REPL session under today's code:

```
> approval default prompt
> run write a hello script to /etc/motd
Write: /etc/motd
  1 lines, 14 bytes
Execute? [y/N/?] _          ← blocks forever under `nb prog.nb </dev/null`
```

After:

```
> run write a hello script to /etc/motd
[nb] denied: write_file → /etc/motd is outside the working directory.
     Authorize with: approval path /etc          (or --trust for cwd + temp)
✗ write_file denied
```

and what the model receives:

```
Error: write_file was denied. /etc/motd is outside this run's authorized paths,
and nothing in the approval policy allows it. This will not succeed on retry —
work within the working directory, or report that the path is unauthorized.
```

The human's fix is a directive they can paste straight into the program file. That is
strictly better than a keypress: the REPL is nb's *authoring* surface, so authorization
belongs there as text.

## What changes

**1. One denial helper, two audiences.** The split `DenyNonInteractive` /
`DenyByPolicy` already gesture at:

- *Model-facing* (the `ToolOutcome` string): what was refused, that retrying will not
  help, and what to do instead. The existing "do not retry; try a different approach" is
  the right shape and should be kept.
- *Human-facing* (stderr): the same refusal plus **the directive or flag that would
  authorize it**. This is the half that does not exist today — the current message says a
  call was denied but not how to permit it.

**2. `ApprovalDecision` collapses to `Allow` / `Deny`.** `Prompt` has no meaning once
nothing prompts. The two denial *reasons* (nothing matched under the standard ladder vs.
explicit `default deny`) survive as message text, not as control flow.

**3. `NonInteractive` and the `Console.IsInputRedirected` gate disappear.** Runs stop
depending on whether stdin is a pipe. `never hangs` stops being a headless-only property
and becomes unconditional.

**4. Seven call sites shed their prompt blocks.** `HandleBashToolCall`,
`HandleWriteFileToolCall`, `HandleEditFileToolCall`, `HandleSearchWebToolCall`,
`HandleFetchUrlToolCall`, `HandleApplyPatchToolCall`, `ApproveReadPath`. Each keeps its
classification, its diff/danger display and its console chrome — that output is useful to
a human reading a transcript, and `approval-is-not-a-boundary` Tier 1 explicitly keeps
the dangerous-command *classification* for exactly this reason. Only the keypress goes.

**5. `ApprovalDefault` keeps its value names** (decided 2026-08-15). `Prompt` vs `Deny`
under-describes the difference — the real distinction is which auto-approve ladder runs —
but the names are published grammar (`approval default deny`). The XML doc was corrected
in the hygiene commit to describe them as permissiveness tiers.

## Relationship to the two existing approval plans

**`plans/approval-is-not-a-boundary.md` (proposed).** This plan is a component of it and
shares its premise. Two points of contact:

- *Reinforces:* that plan's Tier 2 keeps `ApprovalDecision.Deny` "exactly as-is," calling
  a recorded, transcript-visible refusal "the load-bearing feature… the one part of the
  subsystem that gets *more* important." This plan makes `Deny` the only non-allow
  outcome, which is that position carried to its conclusion. Its sequencing item 0
  (emit `approved` on `tool_call`, denial counts in the `result` trailer) is where these
  refusals become visible; land that first and this plan's output has somewhere to go.
- *Tension to resolve:* that plan justifies keeping `TrustSandbox` partly because "the
  interactive REPL still has a human who reasonably wants 'don't touch things outside what
  I pointed you at.'" This plan agrees and does not remove that. The path rule stays as an
  *auto-approve convenience default*; what goes is the interactive *escalation* when the
  default does not match. The REPL human keeps "don't touch things outside what I pointed
  you at" — they lose only "…but ask me and I'll say yes." Those are separable, and the
  boundary plan's own framing (a UX preference, not a boundary) survives intact.
- *Naming collision to avoid:* that plan proposes renaming `SafeCommandPrefixes` to
  `NoPromptPrefixes`. If prompts are gone, that name is stillborn. Prefer
  `RoutineCommands` (its other suggestion).

**`plans/approval-policy-and-sandbox.md` (active).** Superseded in part. Its §"The
interactive prompt UX is unchanged; the policy only chooses Allow/Prompt/Deny" and its
`Prompt → if NonInteractive, treat as Deny` rule are exactly what this plan retires. The
policy/call-site split it established is kept — the policy still decides, the call site
still renders.

## Open questions

1. **Does `--trust` become the interactive default?** DECIDED 2026-08-15: **no.** Defaults
   do not change; `--trust` is documented as the interactive ergonomic path, and it is
   recorded rather than ambient.

   The governing reason is what the REPL is *for*: walking a program step by step to
   monitor what is actually going on. It is an observation instrument, not a place to
   change behaviour or silently adopt new behaviour. A keypress that decides whether a
   tool call executes is the instrument altering the thing it was built to observe — so
   the prompt is not merely unnecessary at the REPL, it is contrary to the REPL's purpose.
   A REPL-only permissive default would be the same defect wearing a config flag.
2. **Do refusal strings become costume-overridable?** Logged as a TODO under Harness
   emulation, deliberately deferred: no `virtual` until there is a second implementation.
   This plan's only obligation is to put the helper somewhere that *can* become virtual —
   a static on `NbHarness`, not a free function elsewhere.
3. **Does the rejection-reason prompt have a replacement?** DECIDED 2026-08-15: **not
   now** — accept the loss. A human typing prose into a live denial to steer the model is
   chat-client behaviour, and it is the one part of the prompt loop that was a genuine
   feature of the thing nb stopped being.

   Recorded as a future direction rather than a gap to close: let a **program** author the
   refusal text, so a run can stage a rejection history deliberately — *"you tried three
   approaches and the user rejected each one; you are now looking for a fourth. What do
   you choose?"* That is a real experiment, and squarely nb's business: fabricated premise,
   reproducible, in the program rather than in someone's fingers.

   Note the retrospective half may already be expressible — a program can fabricate a
   prior tool round the model believes it made, including a denied one. What does not
   exist is program-authored text for a denial that happens *live* during a run. Scope
   that gap before building anything; it may be smaller than it looks. Future work, not a
   prerequisite for this plan.
4. **Exit code `4` is unreachable — implement it or retract it.** Checked 2026-08-15; the
   answer is neither option the question assumed. `ExitReasons.ApprovalDenied` is defined
   and mapped to exit 4, but **nothing assigns it**. The only producers are `Ok`,
   `MaxToolCalls`, `ProviderError`, `RateLimited`, `TimeBudget`, `TokenBudget` and
   `ToolErrorLimit`. A denied call becomes a `ToolOutcome.Fail`, so a run that hits
   denials exits `0` (the model routed around it) or `3` (`tool_error_limit`) — never `4`.

   Both published specs promise it: `docs/conversation-program-cli.md:86` and
   `docs/conversation-program-api.md:263`. The latter is the doc owed to the downstream
   consumer, who may already be branching on an exit code that cannot occur.

   This plan forces the decision, because denial stops being an edge case and becomes the
   only non-allow outcome. Two honest endpoints:
   - **Implement it** — a run whose terminal failure was a denial exits 4. Needs a rule
     for "denied but recovered" (recommend: exit `0`, with the denial visible in the
     trailer per `approval-is-not-a-boundary` item 0).
   - **Retract it** — delete the constant and both doc rows; denials ride
     `tool_error_limit` like any other tool failure.

   Recommend implementing. A caller distinguishing "the model was not authorized to
   proceed" from "the model kept failing at its task" is the whole reason the constant was
   written, and that distinction gets *more* useful under this plan, not less.

## Steps

1. **Docs first** (the corpus outvotes the decision otherwise). `docs/conversation-program-cli.md`:
   make "never hangs" unconditional, drop the headless qualifier from the denial rules.
   `CLAUDE.md` + `README.md`: remove the interactive-approval UX descriptions. No code.
2. **The denial helper.** One method, two audiences, actionable human text. Land it
   *alongside* the existing prompts, used by the already-non-interactive paths, so its
   message wording gets reviewed before the deletion depends on it.
3. **Delete the seven prompt blocks**, keeping classification and display. `ApprovalDecision`
   collapses to `Allow`/`Deny`; `NonInteractive` and its uses go.
4. **Tests** — the coverage that never existed: an unmatched call denies identically with
   stdin a pipe and a TTY; the denial names the authorizing directive; `approval default
   deny` and standard-ladder denials produce distinguishable messages; a denied run's exit
   code matches the documented one.
5. **Golden masters.** Denial strings are model-visible, so the tool-surface goldens may
   need a denial companion. Decide whether refusals belong in a golden file — they are
   observation-channel text, which argues yes.

## Done test

Run the same program three ways — piped, from a file at a TTY, and from the REPL — with a
call nothing authorized. All three produce the same transcript, the same exit code, and a
denial message that names the directive which would have allowed it. If any of the three
differs, or if a reader has to know whether stdin was a pipe to predict the outcome, it
did not land.
