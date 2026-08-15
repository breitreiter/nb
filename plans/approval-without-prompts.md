---
kind: plan
title: Approval without prompts — refusals the model can read
created: 2026-08-15
updated: 2026-08-15
status: current
state: accepted
touches:
  files:
    - nb.Tests/DenialGoldenTests.cs
    - nb.Core/ToolErrorTracker.cs
    - nb.Tests/ApprovalDenialTests.cs
    - Providers/Mock/MockProvider.cs
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
[nb] denied: write_file → /etc/motd — nothing in the approval policy allows it.
       No approval directive grants paths outside the working directory. Work within
       it, or start nb from a directory that contains the target.
```

> **Corrected 2026-08-15 during step 2.** This example originally offered
> `approval path /etc` (and `--trust`). Neither exists: the approval grammar is
> `bash | mcp | search | fetch | default | sandbox`, and trust is config-only. Naming a
> directive that cannot grant the thing is the exact defect this plan's step 2 exists to
> fix, so the example now shows the honest refusal. If a path-widening directive is
> wanted, that is its own piece of work — this plan does not deliver one.

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

   ~~Keep that answer~~ **REVERSED 2026-08-15** by the step 6 capture. The deferral rested
   on there being no second implementation; there are three, and they disagree — `claude-code`
   is human-in-loop, `codex` is escalatable, `qwen-code` is terminal. That is exactly the
   "second implementation" the no-`virtual` rule was waiting for, and it arrived as evidence
   rather than speculation. Make the refusal helper `virtual` on `NbHarness` and override it
   per costume.

   The rule itself is unchanged and was applied correctly — the abstraction is justified by
   an observed difference, not by anticipated flexibility. Note that keeping one shared
   string is not the neutral choice here: it would ship nb's own refusal class under every
   costume and erase a difference that moves model behaviour.
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

   DECIDED 2026-08-15: **implement it.** A run whose terminal failure was a denial exits
   `4`. A denial the model recovered from exits `0`, with the denial visible in the trailer
   per `approval-is-not-a-boundary` item 0 — which is the prerequisite below, and the reason
   "recovered" is expressible at all. Both published specs keep their promise unchanged.
   Resolves `bugs/Approval_Denied_Exit_Code_Is_Unreachable.md`.

## Steps

**Prerequisite (decided 2026-08-15): `approval-is-not-a-boundary` item 0 lands first** —
emit `approved` on `tool_call` (`allow`/`deny` plus the reason label) and count denials in
the `result` trailer. Not a nicety: this plan makes denial the only non-allow outcome, so
without item 0 refusals become load-bearing and transcript-invisible at the same time —
the legibility gap this plan exists to close, relocated rather than fixed. It is also what
makes "denied but recovered" observable, which open question 4's exit-code rule depends on.
That plan flags item 0 as additive and independently valuable, so taking it first costs
nothing even if the rest of it is never built.

1. **Docs first** (the corpus outvotes the decision otherwise). `docs/conversation-program-cli.md`:
   make "never hangs" unconditional, drop the headless qualifier from the denial rules.
   `CLAUDE.md` + `README.md`: remove the interactive-approval UX descriptions. No code.
2. **The denial helper.** One method, two audiences, actionable human text. Land it
   *alongside* the existing prompts, used by the already-non-interactive paths, so its
   message wording gets reviewed before the deletion depends on it.

   Two defects in the current strings the helper must not inherit (found 2026-08-15):
   - **They assert a user who does not exist.** Seven read `Error: User rejected this
     command. Permission denied.` After this plan there is no user and no keypress — the
     refusal comes from policy. This is not a wording nit: it puts nb in the
     *human-in-loop* refusal class (see step 6) when the plan is deliberately moving it to
     *terminal*, and a model that believes a person declined may stop to address them.
   - **They name flags that do not exist.** `NbHarness.cs:691` tells the model no
     pre-approval `(--approve/--trust)` matched. Neither flag is in the codebase; trust is
     config-only and there is no `--approve`. Stale model-visible text that gestures at
     authorization without naming a real one — exactly what the human-facing half is
     supposed to fix.
3. **Delete the seven prompt blocks**, keeping classification and display. `ApprovalDecision`
   collapses to `Allow`/`Deny`; `NonInteractive` and its uses go.

   *Done 2026-08-15.* Net −292 lines. Three corrections to what this step assumed:

   - **There were eight, not seven.** The eighth is the MCP approval loop in
     `ConversationManager.cs`, which had its own `Console.ReadKey` loop, its own
     rejection-reason prompt and its own hand-rolled refusal string — it was not in
     `NbHarness`, so the seven-site survey missed it. It could not have been left behind
     anyway: it gated on `NonInteractive`, which this step deletes. It now refuses through
     the same `Deny` helper as the native tools, which is a bonus the survey did not
     anticipate — MCP denials pick up the costume's refusal class and a pasteable
     `approval mcp {tool}` remedy instead of the old "User rejected this tool call".
     `Deny` became `public` for this (it was `protected`), still `virtual`.
   - **`ApprovalDefault` keeps the name `Prompt`.** Only `ApprovalDecision` collapsed. The
     tier still decides how far a call climbs before counting as unmatched (`Deny` skips
     the safe list and trust), so the distinction is live — and `prompt` is the wire
     spelling in both `approval default prompt` and `"Default": "prompt"`. Renaming it
     would break published grammar to fix a comment. It is documented as "the permissive
     tier" instead.
   - **The rung had to move off the decision.** Call sites used to read
     `decision == Deny` to mean "the program asked for `default deny`" and `NonInteractive`
     to mean "nothing matched". With the decision collapsed, both are `Deny` and neither
     survives — so `NbHarness.DenyRung` now reads `ApprovalPolicy.Default` directly. That
     is what keeps `default-deny` and `no-match` distinguishable in the ledger, which
     step 4 tests.
4. **Tests** — the coverage that never existed: an unmatched call denies identically with
   stdin a pipe and a TTY; the denial names the authorizing directive; `approval default
   deny` and standard-ladder denials produce distinguishable messages; a denied run's exit
   code matches the documented one.

   *Done 2026-08-15.* `nb.Tests/ApprovalDenialTests.cs` (9 tests, facade-driven) plus five
   `ToolErrorTrackerTests` cases. 557 green, up from 542. Notes:

   - **Exit code 4 is now reachable**, resolving
     `bugs/Approval_Denied_Exit_Code_Is_Unreachable.md`. `ToolErrorTracker` learned
     `isDenial`, and a streak that trips the limit reports `approval_denied` (exit 4) when
     *every* failure in it was a denial, `tool_error_limit` (exit 3) otherwise. Unanimity
     is deliberate: a run that mixed real failures with denials is not an authorization
     problem, and calling it one sends the caller to fix the wrong thing. Denials still
     count toward the same error budget — that is what bounds a model hammering a refused
     call; only the *reason* the budget tripped is new.
   - **The pipe-vs-TTY test could not be written as behaviour.**
     `Console.IsInputRedirected` is not injectable, so the only way to make the two cases
     provably identical was to delete the branch that read it. The test asserts the
     deletion instead (no `NonInteractive`-shaped member on `NbHarness`; `ApprovalDecision`
     has exactly two members). That is a regression guard, not a proof — worth naming,
     because CI runs under a pipe and would not catch a reintroduced TTY check any other
     way.
   - **`MockProvider` gained `fetch_url` and `write_file` argument arms.** Both previously
     fell through to `["input"] = arg`, so a scripted call reached the tool with an empty
     url/path — which silently made the very paths this step tests untestable. Watch for
     the same gap in `grep`, `find_files` and `edit_file` when step 5 needs them.
   - **Gotcha for step 5: `dotnet test` alone can run against a stale mock.**
     `Providers/Mock` is loaded at runtime through `AssemblyLoadContext`, so it is not in
     the test project's dependency graph and `dotnet test` will not rebuild it. A mock
     change needs `dotnet build` first, or the suite tests the previous DLL. This cost a
     confusing red run here.
5. **Golden masters.** Denial strings are model-visible, so the tool-surface goldens need a
   denial companion. Settled by the step 6 capture: refusals are per-costume
   model-visible text, which is precisely what the goldens exist to pin. One denial golden
   per costume, covering both denial reasons.

   *Done 2026-08-15.* `nb.Tests/DenialGoldenTests.cs` + four
   `nb.Tests/golden/denial.*.txt`. Each costume gets the same four *situations* — shell
   command, write outside the sandbox, network fetch, read outside the sandbox — spelled
   in its own wire vocabulary, under both denial reasons. Codex gets two (shell,
   apply_patch): it withholds the file and network tools, so that is its whole refusable
   surface. Captured at `NbHarness.InvokeAsync`, which is the boundary a costume owns;
   the retry-budget nudge `ConversationManager` appends downstream belongs to the error
   tracker and pinning it in four files would couple these goldens to an unrelated
   subsystem. Both halves recorded — model-facing result *and* human-facing stderr.

   **The baseline immediately found a defect the plan had not anticipated.**
   `denial.nb.txt` and `denial.claude-code.txt` are byte-identical apart from the `===`
   case headers — expected for refusal *class*, since step 6's overrides have not landed.
   But they are also identical in a way that is wrong regardless of class: **the refusal
   names nb's canonical tool, not the name the model actually called.** A model wearing
   the claude-code costume calls `Write` and is told `write_file → /etc/motd was denied`;
   under codex it calls `shell_command` and is told `bash (Read): /etc/passwd`. Both name
   a tool the costume never advertised.

   This is the same shape as the already-filed "pending-todos reminder names a tool the
   costume doesn't advertise" (TODO.md, Harness emulation) — model-visible text leaking
   nb's own vocabulary through a costume that exists to hide it. It is arguably worse
   here: the todo nudge is advice, whereas a refusal is the only thing a blocked model
   gets, and a refusal citing an unknown tool invites it to retry under the name it does
   know. **Fold into step 6** — `Deny` is going virtual anyway, and the wire name has to
   reach it either way, so the two changes touch the same signature. The goldens will show
   both corrections in one reviewable diff.
6. **Capture what real harnesses say when they refuse.** The blank spot in the emulation
   corpus, and the fact that decides open question 2.

   *Why it is missing.* Every other model-visible result string carries provenance in
   `plans/harness-emulation.md` — shell output, write/edit results, environment context and
   project-instruction discovery are each marked **verified** (read against `openai/codex`,
   `codex-rs/core/src/agents_md.rs`) or **observed** (first-hand capture), and each has a
   `virtual` formatter that costumes override. Refusals have none: no provenance note
   anywhere in `plans/`, `bugs/` or `imp/`, no override, all seven strings hard-coded in
   `NbHarness`. The one acknowledgement is `CodexHarness.Omissions` (`CodexHarness.cs:128`),
   which records the approval surface as *deliberately skipped* because mapping nb's model
   onto Codex's permission-profile vocabulary "would mean inventing enum spellings."

   *The fidelity bar is deliberately low.* The goal is not simulation. It is that a problem
   you hit in nb is one you would also hit in Claude Code or Codex. A refusal string is
   short and does one job, so a plausible example found in docs, an issue thread or a blog
   post is enough, and a stale one is fine — six months does not change what "permission
   denied" means to a model. This is unlike tool *schemas*, where the model must match
   names and parameters syntactically and a stale capture yields malformed calls. Do not
   spend real effort chasing byte fidelity here.

   *The one axis worth getting right* is not wording but what the refusal implies about the
   next move. Three classes, which produce measurably different trajectories:
   - **terminal** — will not succeed on retry; route around it. (What this plan makes nb.)
   - **escalatable** — retry with elevated permission. A model reading this burns turns
     retrying, which is a distinct and visible transcript shape.
   - **human-in-loop** — a person declined; ask them. (What nb's current strings wrongly
     signal — see step 2.)

   *Captured 2026-08-15.* Better sourcing than the low bar asked for — two of the three read
   from source rather than inferred. **The three costumes diverge across all three classes.**

   - **`claude-code` — observed.** Verbatim, quoted consistently across
     `anthropics/claude-code` issues #40156, #29238, #29499:

     > The user doesn't want to proceed with this tool use. The tool use was rejected. STOP
     > what you are doing and wait for the user to tell you how to proceed

     **human-in-loop**, and unusually strong about it — an explicit stop-and-wait
     instruction. Issue #40156 is a report that the model *ignores* it and retries the
     denied call, so the behavioural pull of this string is itself contested.

   - **`codex` — verified.** Sandbox failure surfaces as `command failed; retry without
     sandbox?`, and the model re-issues the call with `with_escalated_permissions=true`
     (`codex-rs/core/src/tools/orchestrator.rs`,
     `codex-rs/core/src/tools/runtimes/shell/unix_escalation.rs`; issues #19162, #18079).
     **escalatable** — and the retry is a first-class parameter on the tool call, not just a
     tone in the prose. Under `approval_policy=never` the denial simply fails back to the
     model.

   - **`qwen-code` — verified.** Read from
     `packages/core/src/core/coreToolScheduler.ts` (`ToolErrorType.EXECUTION_DENIED`):

     > Qwen Code requires permission to use "{tool}", but that permission was declined.
     > Matching deny rule: "{rule}".

     with a distinct non-interactive variant: `…but that permission was declined
     (non-interactive mode cannot prompt for confirmation).` **terminal.**

   *Three things this changes.*

   1. **Question 2 is answered, and answered the other way.** The costumes do not agree —
      they occupy all three classes. Refusal class is a real emulation fact, not shared
      furniture, so the refusal helper earns a `virtual` and per-costume overrides. Keeping
      one shared string would silently give every costume nb's own class and erase a
      difference that demonstrably moves model behaviour.
   2. **nb currently ships the `claude-code` refusal for every costume.** `Error: User
      rejected this command. Permission denied.` is the human-in-loop class in all three
      costumes — accidentally right under `harness claude-code`, wrong under `codex` and
      `qwen-code`, and wrong for bare nb once this plan lands. Step 2's fix and this
      override are the same work.
   3. **Naming the authorizing rule belongs in the model-facing half too.** `qwen-code`
      puts `Matching deny rule: "{rule}"` in the string the *model* reads, not just the
      human's console line. This plan currently assigns that job only to stderr. A real
      harness does both, and it costs nothing — a model told which rule blocked it can
      report the blocker accurately instead of guessing.

   Land the three rows in the `harness-emulation.md` provenance table under the existing
   verified/observed convention, and note that `CodexHarness.Omissions` (`CodexHarness.cs:128`)
   can drop its approval-surface caveat for refusals specifically — the escalation
   vocabulary is now sourced, even though the permission-*profile* enum spellings it
   actually refers to remain out of scope.

## Done test

Run the same program three ways — piped, from a file at a TTY, and from the REPL — with a
call nothing authorized. All three produce the same transcript, the same exit code, and a
denial message that names the directive which would have allowed it. If any of the three
differs, or if a reader has to know whether stdin was a pipe to predict the outcome, it
did not land.

*Run 2026-08-15, after step 3.* Piped and file-at-a-TTY produce **byte-identical** JSONL —
same `tool_call` with `"approved":"deny"`, same refusal text naming `approval bash cat *`,
same `"denied":1` trailer, same exit `0` (the model recovered, which is the documented
rule). The REPL leg **was not driven**: this box has neither `expect` nor `tmux`, and
feeding a file through `script` crashes UglyPrompt's line editor, which answers its own
`ESC[6n` cursor query with nothing and then indexes a negative column. That crash
reproduces identically at `bda08d0` (before step 3) and is in the line editor, not
approval — but the leg is untested, not passed. Drive it by hand, or once a pty driver is
available.
