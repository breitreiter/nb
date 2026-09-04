---
kind: plan
title: Oracle resolver — servicing the halt when a model asks the void a question
created: 2026-08-19
updated: 2026-08-19
status: exploratory
state: idea
touches:
  files:
    - nb.Core/ConversationManager.cs
    - nb.Core/ProgramEvaluator.cs
    - nb.Core/Transcript/ProgramParser.cs
    - nb.Core/Harness/NbHarness.cs
  features: [tool-surface, transcript, evals]
provenance:
  author: claude
  note: exploration only — not committed to, not scheduled
---

# Oracle resolver — servicing the halt when a model asks the void a question

## Context

Sonnet likes to pause and ask clarifying questions. That is fine, and often correct.
It is also a problem for evals: a run that halts on a question is a run that ended,
so a multi-step task needs either (a) an instruction never to ask — which perturbs
the thing under test — or (b) many small scripted programs stitched around each
expected pause.

Today nb makes this worse than it needs to be. `ConversationManager.cs:660`, the
repetition-breaker, tells a looping model: *"stop and ask the user for
clarification."* nb actively encourages a halt it has no mechanism to service. No
costume advertises an ask-user tool either — `claude-code.md`, `codex.md`,
`qwen-code.md` all omit it — so the model's only route is prose, which is the
hardest kind to detect.

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
- **Fix the repetition-breaker.** `ConversationManager.cs:660` should not advise
  asking a user who does not exist. Independently worth doing regardless of this plan.

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
