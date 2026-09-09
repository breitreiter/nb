---
kind: plan
title: Oracle resolver — servicing the halt when a model asks the void a question
created: 2026-08-19
updated: 2026-09-09
status: proposed
state: build order steps 1-2 landed; next action is step 3 (the oracle call)
touches:
  files:
    - nb.Core/ConversationManager.cs
    - nb.Core/ProgramEvaluator.cs
    - nb.Core/Transcript/ProgramParser.cs
    - nb.Core/Harness/NbHarness.cs
    - nb.Core/OracleResolver.cs
    - nb.Core/AnswerSheet.cs
    - nb.Core/Transcript/OracleProtocol.cs
    - Providers/Mock/MockProvider.cs
  features: [tool-surface, transcript, evals, prompt-testing]
provenance:
  author: claude
  note: 2026-08-19 exploration; 2026-09-09 revision commits to a v1 shape and builds it (see Revisions)
---

# Oracle resolver — servicing the halt when a model asks the void a question

## Status — read this first if you are picking this up cold

**Nothing is built.** No `oracle` directive, no `OracleEvent`, no resolver. The design
below is settled at v1 (see *Revisions*, 2026-09-09) and the next action is step 1 of
*Build order*. This section exists so a fresh session does not re-derive what a
2026-09-09 scoping pass already established.

**The REPL gate is lifted (2026-09-09, `48683d7`).** This section used to say *do
`plans/retire-the-repl.md` stages 3-5 first*, because that plan's **open question 3** —
*does `ProgramEvaluator.EvaluateEventAsync` stay public?* — governed the very method
whose `RunEvent` case this plan's oracle loop goes into (step 4). That plan is now
`state: done`, and it answered the question the way this one needs: `EvaluateEventAsync`
**stays public**, and `--output interactive` keeps its name. The loop can be built into a
method whose contract is settled.

The deletion also removed the awkward caller. The REPL was the only thing that would have
driven per-line oracle continuations, so the loop no longer has to answer what a
continuation means outside a program — there is one execution mode, and `oracle_turns` is
scoped to a single program run. Nothing else in this plan's file set moved: retirement
touched `Program.cs`, `FileMentionSource.cs`, `nb.csproj`, `TrustSandbox.cs`, and this
plan touches none of those. Design below is unchanged.

**Verified code anchors** (checked 2026-09-09; re-confirm before relying on a line
number, but the shapes were true then):

| what | where | note |
| --- | --- | --- |
| where the loop goes | `ProgramEvaluator.cs`, `case RunEvent` | gate on `_conversation.LastOutcome == ExitReasons.Ok` |
| record to copy | `HarnessEvent` in `Transcript/TranscriptEvent.cs` | one-field envelope directive; `BudgetEvent` is the two-field form |
| serializer arms | `Transcript/TranscriptSerializer.cs` | one `case` to write, one to read; both switch on the type string |
| `budget` parse | `Transcript/ProgramParser.cs` `ParseBudget` | accepts **any** key; semantic validation is the switch in `ProgramEvaluator.ApplyBudget` — so `oracle_turns` is one `case` in each |
| `@file` include | `Transcript/ProgramParser.cs` `ResolveContent` | whole-content `@path` only, no whitespace; already what the sheet needs |
| exit codes | `Transcript/ExitReasons.cs` `ToExitCode` | note the `_ => 0` fallback — see the implementation note about writing both arms explicitly |
| side-call precedent | `ConversationManager.cs`, the summarisation call | builds its own message list against `_client`; the oracle call is the same move |
| the reminder to fix in step 4 | `ConversationManager.cs`, the doom-loop `system_reminder` | currently ends *"No one is available to answer a question mid-run."* |
| Mock dispatch | `Providers/Mock/MockProvider.cs` | keys off the last **user** message — the reason step 2 exists |
| step 2's precedent | `MockProvider.cs`, the `MOCK:loop=` handler | already scans **all** user turns rather than the last one, "so an injected loop/todo reminder can't derail it" — the rider convention step 2 proposes is the same move, not a new one |

**Superseded findings from that pass**, so they are not rediscovered as news:

- The old claim that nb's *own* prompt encourages asking is dead twice over. The
  repetition-breaker was fixed independently, and `prompts/system.md` turned out to
  be unreachable — `ConfigurationService.GetSystemPrompt()` has no callers, and the
  bare surface has no preamble (`NbHarness.Preamble` is `null`). Deleting that file
  and its seven provider variants is filed under *Chat-era surface cull* in `TODO.md`
  and is **not** part of this plan.
- Consequently the ask-inducement that remains is **faithful costume reproduction**
  (`prompts/harness/codex.md` says "concisely ask the user if they want you to do so"
  because the real Codex prompt does). That is correct to keep, which is why the
  cheap shortcut — stop telling models to ask — is not available and a resolver is
  warranted.

## Context

Sonnet likes to pause and ask clarifying questions. That is fine, and often correct.
It is also a problem for evals: a run that halts on a question is a run that ended,
so a multi-step task needs either (a) an instruction never to ask — which perturbs
the thing under test — or (b) many small scripted programs stitched around each
expected pause.

No costume advertises an ask-user tool — `claude-code.md`, `codex.md`,
`qwen-code.md` all omit it — so the model's only route is prose, which is the hardest
kind to detect.

*(Superseded 2026-09-09: this section used to claim nb made the problem worse in its
own voice, citing the repetition-breaker's "stop and ask the user for clarification"
and nb's own `prompts/system.md`. Both have moved. The reminder now says the opposite
— see the invariant below — and `prompts/system.md` turned out to be dead code that
no longer reaches a model at all. The asks nb needs to service are ones it is
**faithfully reproducing** from a real harness prompt, not ones it is manufacturing:
`codex.md:201` says "concisely ask the user if they want you to do so" because the
real Codex prompt does, and editing that to suit nb's evaluator would stop it being a
costume. That removes the cheap shortcut — "just stop telling models to ask" — and
strengthens the case for a resolver.)*

## The reframe

nb has no user. It is a stateless evaluator of a directive document. So "the model
asked a question" is not a conversational interruption — it is **a program that
halted with an unsatisfied dependency**.

That reframe matters because it opens up the resolution space. "Answer it" is one
option among several, and not obviously the best one.

## Shape, if built

An `oracle` directive, parallel to `provider` / `model` / `harness`: it names a
resolution strategy and whatever that strategy needs. A run becomes two
conversation-programs interleaved, each one's output the other's prompt. This is the
version that fits nb's existing grammar without introducing a new concept.

Everything below is a strategy that could sit behind that directive.

## Strategies, cheapest first

### 1. The constant deflection (start here)

Reply to every ask with the same fixed string. No second model, no persona, no
generation:

> Wow, great question. I'm not sure — see if you can work it out with the tools you
> have. If not, put in a placeholder and note it, and we'll fix it later.

Properties that are hard to beat:

- **Zero variance.** Deterministic by construction. Two runs diverge only where they
  always did — at the sample.
- **Zero leakage.** It cannot leak the answer key because it contains no information
  (see *Leakage* below, which is the failure mode that kills the fancier versions).
- **No second model.** No cost, no latency, no oracle-quality confound.
- **It measures something real.** "Does the model proceed sensibly when told nobody
  is home?" is arguably a better question than "does the model finish when handed a
  helpful assistant?" Real users are unavailable all the time.
- **It generalises.** One string works for every task family. A persona brief does not.

The obvious objection — that it's a non-answer and models will re-ask — is testable
in an afternoon and is itself the first result. It also composes with an escalation
ladder: deflect, deflect, then a forced terminal *"proceed on your best judgment and
state your assumptions."*

Note the placeholder clause is doing real work: it gives the model a legitimate
non-blocking action, so the deflection is a redirect rather than a wall. And every
placeholder left behind is a legible artefact of where the model felt underspecified
— arguably better eval data than the question itself.

### 2. Scripted answer table

Answers keyed by ask-index or by topic match. Dumb, and works more often than it
should, because models ask the same three questions.

### 3. Answer bank + selection

Pre-declare a set of answers; an oracle model may only *pick* from them, never
author, plus a fixed "use your judgment" fallback. Cuts variance hard, keeps runs
auditable, and the no-match case is a finding in itself: *the model asked something
we didn't anticipate.*

### 4. Free generation from a persona brief

An oracle model answers in character from a description of what this user wants.
Most flexible, most dangerous (see *Leakage*). If built, cache answers by
question-hash into the seed so replays are stable.

### 5. Refuse, in costume register

Let the ask hit the existing refusal machinery — the refusals that name who to ask —
and measure recovery. Close cousin of strategy 1, but reuses what already exists
rather than adding a resolver.

### 6. Fork both ways

Treat the question as a branch point and run each answer as a `--seed` continuation.
Stops the eval depending on which answer we happened to give, and yields the whole
decision surface instead of one path through it. Tree-shaped and expensive. The
genuinely exotic option.

### 7. Silence

Return nothing, see whether the model self-unblocks. Cheapest probe of a real
behavior.

## Detection

Two mechanisms, and the explicit tool is the *more* faithful one, not the clumsier
one.

**Explicit tool.** If the subject is Sonnet-in-Claude-Code, the real product surface
has AskUserQuestion. Not advertising it is the perturbation. It gives a crisp halt
signal, structured options that an answer bank can key on directly, and costume
fidelity in one move. Cost: an observer effect the other way — a cheap, advertised
ask raises ask-rate above the real product's. Real, but measurable, and smaller than
the classifier's failure mode.

**Classifier on output.** Works for any model and any costume with no surface change.
Its problem is not accuracy in the abstract — it is that the ambiguous cases are the
entire population. *"Done — let me know if you'd also like me to handle X"* is
simultaneously a completion and a question. A false positive there injects a
resolution into finished work and manufactures busywork.

**Hybrid.** Tool as fast path, classifier as backstop for models that ignore the tool
and ask in prose. The gap between them is a metric worth having on its own: *how
often does the model ask in prose despite holding the tool* is a genuine
harness-fidelity finding.

Also worth keeping distinct: "is it asking?" and "is it done?" are different
classifications. Conflating them is a bug. A turn can be done, asking, refusing,
stalled, or silently truncated.

## Leakage — the failure mode that kills strategies 3–4

If the oracle knows the task's ground truth and the model asks *"should I use
approach X?"*, the oracle says yes and you have evaluated the oracle. Question-asking
becomes a side channel to the answer key, and the models that ask most score best —
the exact inversion of what the eval measures.

The barrier has to be hard: the oracle gets a **persona brief** (what this user wants,
their constraints, preferences) and never the rubric, never the reference solution. A
user knows what they want, not how to do it. A design that skips this barrier is
worse than abandoning the run on the first question.

Strategies 1, 2, 5, 7 sidestep this entirely by carrying no task-specific
information. That is the strongest argument for starting at the cheap end.

## Invariants, whichever strategy

- **Never paper over the ask.** Every one is a first-class transcript event with its
  full text. Asks-per-task, ask quality, and time-to-first-ask are results, not noise.
  The accumulated corpus of *what does this model get confused about in this task
  family* is plausibly worth more than the pass rate it was blocking.
- **Budget the loop.** Vague resolution → re-ask → vague resolution is the obvious
  infinite. Cap it, escalate specificity, then force a terminal proceed-anyway.
- **The repetition-breaker has to know whether anyone is home.** This was filed here
  as *"stop advising a user who does not exist"*, and that half landed independently:
  the loop reminder in `ConversationManager.cs` now ends *"No one is available to
  answer a question mid-run."* The fix inverted the problem rather than closing it.
  With an `oracle` in the envelope someone **is** available, and nb is injecting a
  sentence that suppresses the asks the oracle exists to service — a model-visible
  string that silently perturbs the run. The reminder needs an oracle-aware branch
  before the resolver is worth measuring.

## The baseline this has to beat

**Fabricated history.** Seed the program with a prior exchange in which the user
already answered the likely questions. It perturbs the run — but *less* than a
system-prompt rule, because it is in-band and it is exactly what a real user would
have done. Zero code, available today.

Any version of the oracle resolver should be made to justify itself against that
baseline, and strategy 1 is the only one cheap enough to be an easy call.

## Open questions

- Does deflection actually unblock Sonnet, or does it re-ask? (One afternoon to find out.)
- Does advertising an ask tool change ask-rate enough to invalidate costume fidelity?
- Should asks count against `MaxToolCalls`, or have their own budget?
- Is the placeholder artefact worth asserting on in evals directly?

---

## Revisions

### 2026-09-09 — v1: answer sheet + conservative continuation

**Why the calculus changed.** The exploration above was written for coding-agent
evals, where the baseline to beat was fabricated history and the cheapest strategy
was constant deflection. The motivating case is now **prompt testing**: prompts under
development that turn into multi-turn conversations, where the model's questions to
the user are part of the flow being exercised. Neither baseline fits that. Seeding
the answers as history reshapes the very conversation under test, and deflection says
nothing about whether turn three works. So a real oracle is justified, and it is
strategy 3 (answer bank + selection) — the sheet is the centre of gravity.

**Constraints this design accepts:**

- nb already holds a client to a model; a small side call on it is cheap (precedent:
  the summarisation call in `ConversationManager.cs`, which builds its own message
  list). The side call's prefix is tiny and distinct, so it does not disturb the
  subject conversation's prompt cache.
- The correct answers are authored by a human in a markdown file attached to the
  program.
- Anything not on the sheet ends the run.

#### Directive

One envelope directive, parallel to `harness`, plus a budget:

```
oracle @answers.md          # attach the answer sheet (existing @file mention)
budget oracle_turns 8       # cap on resolutions per run; default modest, not unlimited
```

No `oracle miss` policy in v1. Deflection is dropped (see *Continuation rule*).
Exceeding `oracle_turns` ends the run with `exit_reason oracle_budget` (exit 3, a
limit like the others).

#### Answer sheet format

**Keyed by topic, not by question.** Models phrase the same question many ways, so a
Q/A list matches badly; a fact sheet with headed sections matches well and gives each
entry a stable id for the transcript:

```markdown
## deploy-target
Staging only. Never touch prod during this exercise.

## customer-name
Acme Logistics.
```

This is also the leakage barrier: a sheet holds what a *user knows* (constraints,
preferences, facts about their situation), never the rubric or reference solution.

**The event carries the resolved sheet body, not the path.** `@answers.md` is
expanded at parse time (the whole-content include in `ProgramParser`) and the text
travels on the `OracleEvent`. This follows the costume preamble, which materialises
as ordinary system messages *"so a `--seed` replay reproduces the run even if the
costume, or the project's own instruction file, has been edited since"*
(`plans/harness-emulation.md`). An answer sheet has exactly that property: it is an
input to the run's behaviour, so a transcript that records only a filename records a
run nobody can reproduce once the file moves or changes.

#### Continuation rule — the simplification that makes this buildable

> **Continue only on a confident sheet hit. Everything else ends the run.**

"Is this turn a question or a completion?" is hard in general because the ambiguous
turns (*"Done — want me to also do X?"*) are the bulk of the population. Under this
rule that boundary stops mattering:

- A **false positive** costs nothing: the run ends exactly as it would have without
  an oracle.
- A **false negative** requires missing a *clear* demand for information that *has*
  a sheet entry — the easy case.

The error has been moved into the corner where it is cheapest. There is no deflection
rung, no "I'm not sure" reply, no escalation ladder: those exist to handle the
ambiguous middle, and the rule removes the middle.

Outcomes after a run ends `ok`:

| Oracle verdict | Effect | `exit_reason` |
| --- | --- | --- |
| clear demand, sheet entries selected | append a `user` turn, run again | (continues) |
| clear demand, nothing on the sheet | end | `oracle_miss` |
| not clearly waiting on the user | end | `ok` |

`oracle_miss` is kept as a label even though the flow is identical to `ok`: it is the
maintenance signal that the sheet needs an entry, or that the prompt produced a
question nobody anticipated. Both are findings.

#### One oracle call does two jobs

Classification and selection collapse into a single side call, scoped to the sheet:

> Here is the assistant's last message, and a sheet of headed entries. Reply with the
> ids of the entries that resolve what it is clearly asking the user for; `DONE` if
> it is not clearly waiting on the user; `MISS` if it clearly is but nothing here
> applies. Be conservative: when in doubt, `DONE`.

Scoping to the sheet is what makes detection tractable — *"is it asking about one of
these?"* is a far easier question than *"is it asking?"*. The oracle **selects, never
authors**: nb composes the user turn from the selected bodies verbatim, so the
transcript stays auditable and the sheet is the single source of truth. Multi-select
is allowed (one turn may ask two things).

#### Detection: prose only, no surface change, no steer

Considered and rejected for v1: steering the model toward the costume's ask-user tool
(AskUserQuestion or equivalent) via an injected system rule, so the ask is overt.

- **It perturbs the thing under test.** The subject is a prompt; a system-level
  "call ask_user when you need information" changes its multi-turn behaviour, and the
  exploration above already notes an advertised ask raises ask-rate.
- **Coverage is partial.** No costume in this repo currently advertises an ask tool,
  and the bare surface has none. If the tool is missing, no one calls it.
- **It does not remove the prose path.** If the tool is present, there is no
  guarantee it will be called. Models ask in prose while holding the tool, so the
  backstop is needed anyway — the tool buys a fast path, not a simplification.

A costume that does advertise one can feed in later as a second input to the same
oracle call: a call to an ask tool is just an unusually clear demand for information,
with structured options that map onto sheet headings. Additive; no steer required.

#### Wire record

- The oracle's reply enters history as an ordinary `user` message, so seeds replay it
  honestly, carrying enrichment fields `source: "oracle"` and `keys: [...]`
  (ignored on seed-load like other enrichment).
- A miss records the unanswered question in full before the run ends — the
  *never paper over the ask* invariant above still holds.
- The `result` trailer gains `oracle_turns`.
- The doc line "nb never asks mid-run" stays true: the oracle is not nb asking a
  human, it is a scripted user the program declared. The program is the interface.

#### Implementation notes

- Loop lives in `ProgramEvaluator`: after a run ends `ok` and an `oracle` is in the
  envelope, consult it; on a hit, append the user turn and run again, under
  `oracle_turns`.
- **Settle the Mock convention first — it decides whether any of this is testable.**
  `MockProvider` dispatches entirely on the last *user* message, but on the oracle
  side call the last user message is the oracle's own instruction, so every scripted
  `MOCK:` directive is out of scope and the call falls through to the default
  response — which is not a parseable key list. There is no way to script a hit, a
  `DONE` and a `MISS` independently of the subject's reply until this is designed.
  Proposed: the oracle call's prompt opens with a stable sentinel; Mock recognises it,
  scans the message list for a `MOCK:oracle=<ids|DONE|MISS>` rider, and echoes that
  verdict. The rider travels on the subject's own scripted reply — which is precisely
  what the oracle is shown — so one program line scripts both halves:

  ```
  run MOCK:response=Which environment should I deploy to? MOCK:oracle=deploy-target
  ```

  This reuses the sentinel the design already wants for cache-prefix distinctness,
  and keeps the oracle's own selection logic exercised end to end in evals against a
  real model rather than faked in unit tests.
- **Both new reasons get an explicit arm in `ExitReasons.ToExitCode`.** `oracle_budget`
  is a limit → 3, alongside `token_budget` and `time_budget`. `oracle_miss` is → 0:
  the run ended the way it would have without an oracle, and only the label differs.
  Nought is also what the `_ => 0` fallback would produce, which is the reason to
  write it down — an unlisted reason exiting 0 by accident is indistinguishable from
  one exiting 0 by decision, and the next person to add a reason inherits the
  ambiguity.
- ~~The `budget` parse error names `tokens | tool_calls` and already omits `wall_ms`~~ —
  stale as of 2026-09-09: `ProgramEvaluator.ApplyBudget` now warns
  `(tokens | tool_calls | wall_ms)`, so the list is already the real set. Adding
  `oracle_turns` just means keeping it that way.
- Evals should assert on `exit_reason` (`oracle_miss`, `oracle_budget`) and the
  `keys` field — model-visible strings, cheap to see red.

#### Build order

*(Revised 2026-09-09. An earlier ordering opened with "fix the repetition-breaker,
alone", on the theory that it was a standalone bug that stood on its own merits. It
isn't: the reminder says the right thing **today** and only becomes wrong once an
oracle can be declared, so it has no red state until step 4 exists. It moves there.
Nothing now precedes the directive.)*

1. ~~**`OracleEvent` + the `oracle` directive**, carrying the resolved sheet body, plus
   `budget oracle_turns` and the two `ExitReasons` arms. Parse and round-trip only —
   no resolution yet. A program that declares an oracle and never triggers one should
   run, seed and replay identically to one that doesn't.~~ **Done 2026-09-09.** See
   *Step 1 outcome* below.
2. ~~**The Mock convention.** Before the loop, not after: it is what makes steps 3–4
   testable at all, and discovering it doesn't work *after* the loop exists means
   rewriting both.~~ **Done 2026-09-09.** See *Step 2 outcome* below.
3. ~~**The oracle call** — one side call, classification and selection together,
   returning ids / `DONE` / `MISS`.~~ **Done 2026-09-09.**
4. ~~**The loop in `ProgramEvaluator`**, gated on `LastOutcome == Ok`, under
   `oracle_turns` — and with it **the oracle-aware repetition-breaker**, which is the
   first moment the reminder's "No one is available to answer a question mid-run" is
   false. It ships beside the code that falsifies it, so the two can't drift.~~
   **Done 2026-09-09.**
5. ~~**Evals** on `exit_reason` and `keys`.~~ **Done 2026-09-09.** See *Steps 3–5
   outcome* below.

These are a feature, not a fix, so the tests follow the build. The assertions depend
on the sheet format and the wire shape as they actually land, and a test written
first here would encode a guess about an interface still being designed — the case
the discipline explicitly carves out. The reminder change in step 4 is the one
exception: it *is* a string a model reads, and by then the assertion is obvious, so
it takes a red test first like any other fix.

#### Step 1 outcome, 2026-09-09

Landed: `OracleEvent` (one required field, `Sheet`), the `oracle` directive resolving
`@file` through the existing whole-content include, both serializer arms, the
`oracle_turns` budget key, and `OracleBudget`/`OracleMiss` with explicit
`ToExitCode` arms. `dotnet test` 633/633, `evals/run.sh --skip-llm` 76/76.

**The budget key set is written in three places, not two.** The anchors table listed
`ProgramParser.ParseBudget` and `ProgramEvaluator.ApplyBudget`. There is a third — an
up-front validation pass in `Program.cs` (~`:409`) — and it is the load-bearing one:
`ApplyBudget` only *warns* on an unknown key, so with the first two updated and the
third missed, `budget oracle_turns 8` still hard-failed with *"invalid budget key"*.
Caught by running the binary; no unit test covers that pass. All three now carry the
same list and each names the others in a comment.

**`oracle_turns 0` is rejected, deliberately.** That same validator enforces `Value > 0`
for every budget key. An early draft special-cased zero as "declare a sheet that never
continues", i.e. detect asks without servicing them. Dropped: it is a real question but
it needs its own exit-reason story, and it is not worth a quiet exception to a uniform
rule at step 1. Noted in `ApplyBudget`.

**A sheet is `@file`-only in practice.** Source syntax is line-oriented, so an inline
multi-line sheet parses its second line as a directive (`unknown directive '##'`).
Backslash continuation works but nobody will use it. This matches the design's authoring
story — a human-authored `.md` attached to the program — so it is pinned by test
(`Oracle_InlineMultiLineSheet_IsNotExpressible`) and documented rather than fixed.

**Two things noticed in passing.** `ProgramEvaluator.EvaluateEventAsync`'s doc comment
still justified itself by the REPL; it now cites the incremental library host and
`plans/retire-the-repl.md` open question 3. And `ToExitCode` had no test at all, which
made the plan's "write both arms explicitly" instruction unenforceable — `ExitReasonsTests`
now asserts the whole mapping, including that `oracle_miss` and an undefined reason both
give 0, which is the property a future unlisted reason would break.

#### Live-model verification, 2026-09-09 (step 1)

Run against `glmchat` on the local box (`LocalLlm`), since an inert directive's whole
claim is *it changes nothing* and that is a claim about a real model call.

**The sheet does not reach the model.** A 16 KB sheet (~4k tokens, 200 headed entries)
attached to a program, differentially against the same program without it: input tokens
identical at **2552** both ways, output 59 both ways. A behavioural probe agrees — asked
whether a `zzqq`-prefixed sentinel from the sheet appeared in its context, the model said
`NO`. Full evals with the LLM eval enabled also pass.

**But the "seed replay" rationale above overreaches, and the analogy to the costume
preamble is imperfect.** Config directives are not echoed into output: a transcript
captured from a run that declared an oracle contains the conversation and the trailer,
and no `oracle` event. Verified, and it is the same for `budget`. The costume preamble is
*not* a parallel case — it survives a seed because it materialises as real system
messages, which is precisely what the sheet must never do.

So carrying the body on the event buys reproducibility for a **stored JSONL program**
(re-runnable after the sheet file moves), not for a **captured seed**. That is still worth
having and the wire shape is right, but the reason stated in *Answer sheet format* is not
the reason it holds. The actual replay story is step 4's: the oracle's answers enter
history as ordinary `user` turns, and those *are* emitted and *do* replay — which is what
makes a resolved run reproducible. Worth keeping straight before step 4 builds on it.

#### Step 2 outcome, 2026-09-09

The convention landed as proposed, with the sentinel and verdict tokens fixed:

| | |
| --- | --- |
| sentinel | `[nb:oracle-resolver:1]` — **opens the oracle call's last user message** |
| rider | `MOCK:oracle=<ids\|DONE\|MISS>`, scanned across all messages, anywhere within them |
| no rider | `DONE` — the conservative default, so an unscripted program cannot continue by accident |

`dotnet test` 640/640, `evals/run.sh --skip-llm` 82/82 (6 new oracle evals).

**The sentinel is duplicated, deliberately.** `nb.Transcript.OracleProtocol` is the
authoritative declaration for step 3; `Providers/Mock` carries its own copy because it
loads in its own `AssemblyLoadContext` and cannot reference `nb.Core`. Each side's
comment names the other. This is the honest encoding of a wire contract across an ALC
boundary — a shared type is exactly the thing CLAUDE.md's *Assembly Context Gotcha*
warns about. It is versioned (`:1`) so a later change to the oracle prompt's shape can
bump rather than silently redefine it.

**The contract step 3 must honour:** the sentinel is the *start of the oracle call's
last user message*. Mock keys on that, consistently with every other `MOCK:` dispatch.
If step 3 puts the sentinel in a system message instead, the Mock will not recognise
the call and will answer it as ordinary conversation — the failure is silent, so this is
the line to check first if step 3's evals go strange.

**Why the rider is scanned differently from every other `MOCK:` form.** The others use
`StartsWith` on the last user message. The rider arrives *embedded* in quoted text — it
rides on the subject's scripted reply, which is what the oracle is shown — so it is
matched by substring across all messages. That is the same reason `MOCK:loop=` scans
every turn rather than the last one, which was the precedent this copied.

**Testing without the resolver.** Nothing issues a real oracle call yet, so both the
unit tests and the evals drive an oracle-*shaped* call: a run whose prompt opens with
the sentinel. That exercises the whole contract step 3 depends on. The one property that
cannot be asserted end to end yet — that the subject's reply is what gets *shown* to the
oracle — is asserted as two halves joined by hand
(`Oracle_OneProgramLineScriptsBothHalves`), and becomes a single assertion at step 3.

Note the unit tests had to go through the facade rather than construct a `MockChatClient`
directly: the Mock is not in the test project's dependency graph (it is ALC-loaded), which
is the same reason `dotnet build` must precede `dotnet test` whenever provider code changed.

#### Steps 3–5 outcome, 2026-09-09

Built in one pass, because step 3 alone is an unreachable side call and step 4 is what
makes it testable. `dotnet test` 667/667, `evals/run.sh --skip-llm` 94/94 (12 new
oracle evals). Not yet run against a live model — that is the next thing to do, and it
is the only thing that can answer whether a real oracle's verdicts are conservative
enough (see *Open questions* below).

**What landed, and where.**

| piece | where |
| --- | --- |
| sheet parser + verbatim composition | `nb.Core/AnswerSheet.cs` |
| prompt builder + verdict parser | `nb.Core/OracleResolver.cs` |
| the loop | `ProgramEvaluator.ResolveWithOracleAsync`, called after every `RunEvent` |
| the three seams the loop needs | `ConversationManager.SideCallAsync` / `AppendOracleAnswer` / `SetOutcome` |
| wire: `user.source`, `user.keys`, `result.oracle_turns` | `UserEvent`, `ResultEvent`, serializer, mapper, porcelain trailer |
| library: `RunResult.OracleTurns`, `NbProgramBuilder.Oracle(sheet)` | `Facade/` |
| the oracle-aware nudge | the doom-loop reminder, keyed on `SetOracleAvailable` |

**The step 2 contract had a hole, found the moment the loop ran.** The Mock scanned the
*whole* oracle prompt for its `MOCK:oracle=` rider — and the answer sheet is *in* that
prompt. A sheet body is also the subject's next scripted turn (that is how a test chains
asks: `## customer-name` → `MOCK:response=… MOCK:oracle=deploy-target`), so any sheet
that chains carries riders, the Mock found them on every call, and a run that should
have ended `ok` after one hit ran to `oracle_budget`. Fixed by extending the protocol:
`OracleProtocol.SubjectMarker` (`[nb:oracle-resolver:1:subject]`) precedes the judged
text, which comes **last** in the prompt, and the Mock reads riders only after the last
marker. Duplicated in the Mock like the sentinel, for the same ALC reason. With no marker
at all (the step 2 oracle-shaped probes) the whole prompt is still scanned, so those
tests stand unchanged. The general lesson: a rider convention has to say *which text*
is scripted, not just *which call*.

**Verdict parsing is conservative in the same direction as the rule.** Unknown ids are
dropped with a warning; a reply with no readable id and no terminal token is `DONE` with
a warning naming the raw text. Trailing punctuation, backticks and a code fence are
tolerated — models decorate one-word answers. `DONE`/`MISS` match case-insensitively;
ids match the sheet case-insensitively and are recorded in the sheet's own spelling.

**What the oracle is shown.** The sheet (ids and bodies, as `### id` sections) and the
model's **last assistant prose only** — not the conversation. A clear demand for
information is judgeable from the message that makes it, and keeping the prompt small
is what keeps the side call cheap. If live runs show the oracle needs the user's prior
prompt to disambiguate, that is the first knob to turn.

**The oracle is consulted only after `ok`.** A run that ended on a budget, a provider
error or a denial is not judged — it did not halt on the user, and that reason is the
one to keep. Pinned by `Oracle_IsNotConsulted_WhenTheRunDidNotEndOk`.

**Budget semantics.** `oracle_turns` counts *resolutions*, program-wide. The run ends
`oracle_budget` when the model asks again *with a hit* after the budget is spent — a
`DONE` or `MISS` on the (n+1)th ask still ends the run with that verdict's reason, since
nothing was refused.

**The reminder took a red test first**, as the build order said it should
(`LoopReminder_WithAnOracle_SaysToAskPlainly`, observed failing against the old string).
With a sheet attached the nudge now ends *"If you need information from the user, end
the turn and ask for it plainly; an answer may follow."*; without one it still says
nobody is home.

**Usage.** The side call's tokens count toward the session total and the token budget,
like every other round-trip the run paid for. The Mock stamps measured usage on its
oracle reply so a resolved run does not flip the trailer to `estimated`.

**Chrome.** A resolution echoes to stderr as `*(oracle: id, id)* <body>` through the
ordinary markdown renderer, so a human tailing a run sees the scripted user speak.

#### Live-model verification, 2026-09-09 (steps 3–5)

Against `glmchat` on the local box (`LocalLlm`, llama-server), `tools none`, a system
prompt that forces an ask when the target environment is unstated.

**First run: the verdict came back empty.** The subject asked correctly, the oracle
returned `''`, the conservative fallback ended the run `ok` with a warning. Probing the
server directly:

| output cap | result |
| --- | --- |
| 200 tokens | `finish_reason=length`, `content=""`, all 200 tokens `reasoning_content` |
| 2000 tokens | `deploy-target` — correct, after ~1470 tokens of reasoning |
| 200 tokens, `enable_thinking: false` | `MISS` — **wrong** on a plain hit |

A thinking model spends its reasoning *inside* the output cap, so a small cap starves
the verdict, and turning thinking off buys speed with a wrong answer. The cap is now
4096, and an empty verdict with `finish_reason=length` is reported as a truncation
rather than passed off as `DONE` (`ParseVerdict(ChatResponse, …)`). Cost of a verdict on
this model: ~500 output tokens, several seconds. That is the price of a correct
judgement; `budget tokens` bounds it like everything else.

**Then the three cases the design asked for:**

| program | sheet | verdict | `exit_reason` |
| --- | --- | --- | --- |
| forced ask ("specify the cloud platform, environment…") | `deploy-target` = *"staging Kubernetes cluster on AWS (EKS, eu-west-1)…"* | hit | `ok`, `oracle_turns: 1`, the model went on to give the plan |
| same ask | `deploy-target` = *"Deploy to staging."* only | `MISS` | `oracle_miss` |
| same ask | only an unrelated `customer-name` | `MISS` | `oracle_miss` |
| haiku + *"I can also describe production if you'd like"* | full sheet | `DONE` | `ok`, no `oracle_turns` |

The middle row is the interesting one. The model asked for a *cloud provider and
platform*; a sheet that says only "staging" does not answer that, and the oracle said
so. A human would probably have replied "staging" anyway — but the rule prefers a miss
to a dubious hit, and the miss is exactly the maintenance signal the design promised:
*the sheet's entry did not anticipate how the question would be asked*. Widening the
entry to say what the environment actually is turned it into a hit. Sheet authors
should write entries as **what the user would say**, in enough detail to answer the
question several ways.

The last row is the conservative rule doing its job on the ambiguous case that
motivated it: a completion with an optional offer was judged `DONE`, not serviced.

#### Open questions after the build

- ~~**Does a real oracle judge conservatively enough?**~~ On one model and four cases,
  yes — see *Live-model verification* above. n=4 on one local model; the next
  measurement worth taking is a real prompt-testing corpus, counting `oracle_miss`
  against hits and reading each miss.
- **Is the oracle too strict?** The "staging"-only entry was judged a miss where a
  human would have answered. If that pattern recurs on a corpus, the lever is the
  prompt's definition of "covers" — not the rule.
- Should the oracle see the preceding user turn as well as the last assistant message?
- `oracle model <name>` — a cheaper oracle than the subject. Deferred, unchanged.

#### Deferred (bolt on behind the same directive)

`oracle model <name>` (a separate, cheaper oracle model); persona-brief generation
(strategy 4); forking on the answer set (strategy 6); the deflection ladder
(strategy 1) as an opt-in `oracle miss deflect` if a use case ever wants it.
