---
kind: plan
title: nb is an evaluator for conversation-programs
created: 2026-07-07
updated: 2026-07-07
status: current
state: exploring
touches:
  files:
    - Program.cs
    - ConversationManager.cs
  features: [headless, seeds, statelessness, run-specs, library-facade, repl]
provenance:
  author: claude
---

# nb is an evaluator for conversation-programs

## Why this note exists

A pressure-test question exposed that the composable-CLI design (and the
run-spec / seed / task vocabulary it introduced) was carved along the wrong
joints. The question:

> I want to swap provider, set a system prompt, add three lines to history,
> submit a user prompt, and capture only the model's response. Today that's
> six `nb` invocations piped together in a shell script. Is six the best we
> can do?

Six is not the best — the answer is **one**. And the fact that the current
model *needs* six is the tell: it treats configuration, fabricated history,
and the live prompt as three different kinds of thing requiring three
different mechanisms (flags/spec, seed file, prompt arg). They are not three
things. They are one ordered list of directives — a **program** — and nb's
job is to evaluate it.

This note states that thesis. The mechanics (the concrete event schema) live
in `plans/transcript-schema.md`; this note is the *why* those mechanics are
the center of the system rather than an I/O detail. It reframes
`plans/composable-cli-reorientation.md` Pillar 1 (a "spec" is no longer a
distinct artifact) and promotes the transcript schema from "output format" to
"the system's bytecode."

## The thesis

**nb is an evaluator for conversation-programs. The transcript schema is its
bytecode. The REPL, the CLI, and the library are three front-ends that emit
the same bytecode. A "run" is nothing more than the evaluator reaching an
unanswered turn.**

Everything the composable-CLI plan treats as a separate feature — run specs,
seeds, the task prompt, `/save`, the library facade — is a projection of this
one idea. Spec / seed / task are not artifacts; they are *regions* of a single
document.

## The program model

A conversation-program is an **ordered stream of directives**. Two kinds:

- **Config directives** (`config` with `provider`/`model`/`output`/…,
  `system`) — set the envelope going forward. Order matters: a config
  directive governs every directive after it until overridden.
- **Turn directives** (`user`, `assistant`, `tool_call`, `tool_result`) —
  append to the conversation.

And exactly one evaluation rule:

- **A `user` turn with no `assistant` turn after it is *pending*.** The
  evaluator runs the agent loop on it under the current envelope, emits the
  response, and appends it to the conversation. Answered turns are history;
  the unanswered tail is the submission. This is the single convention that
  turns a static transcript into a runnable program — and it is what unifies
  "seed" (fabricated past) and "task" (live prompt): they are the same events,
  distinguished only by whether an answer follows.

There is no third concept. Construction, configuration, and submission are all
directives in one stream.

## The worked example, as one program

The six-invocation flow above becomes one document:

```jsonl
{"type":"config","provider":"anthropic","model":"claude-sonnet-5"}
{"type":"system","text":"You are a terse assistant."}
{"type":"user","text":"fabricated turn 1"}
{"type":"assistant","text":"fabricated answer 1"}
{"type":"user","text":"fabricated turn 2"}
{"type":"assistant","text":"fabricated answer 2"}
{"type":"user","text":"fabricated turn 3"}
{"type":"assistant","text":"fabricated answer 3"}
{"type":"user","text":"the real prompt"}
```

```bash
resp=$(nb < flow.jsonl)      # one invocation; stdout is only the model response
```

The last `user` is the only unanswered turn, so it is the only one that runs.
Everything above it is state the caller constructed. The document is a near
-verbatim transcription of the six original steps.

## Three surfaces, one program

Folding config into the turn stream is what finally makes the "one contract,
three surfaces" claim (composable-CLI Pillar 5) literal rather than
aspirational. The three surfaces become three *encodings of the same
program*:

- **CLI** — the program is the directive stream on stdin (above), or a file,
  or a named prefix (`--spec`) concatenated with piped turns.
- **Library** — directives are method calls; the run-rule is `RunAsync`:

  ```csharp
  var resp = await Nb.Program()
      .Provider("anthropic").Model("claude-sonnet-5")
      .System("You are a terse assistant.")
      .User("fabricated turn 1").Assistant("fabricated answer 1")
      .User("fabricated turn 2").Assistant("fabricated answer 2")
      .User("fabricated turn 3").Assistant("fabricated answer 3")
      .User("the real prompt")     // pending
      .RunAsync();                 // runs it, returns only the response
  ```

- **REPL** — the same program typed live. `/provider` and `/system` are config
  directives; a bare line is a pending `user` turn that runs immediately; a
  new `/assistant "…"` command authors a fabricated turn *without* running
  (the one affordance the REPL lacks today, and the bridge to hand-authored
  seeds). The REPL is simply the evaluator in interactive mode.

The rhyme is exact because all three emit the same bytecode. `/save` is the
proof: it exports a REPL session as the program that reproduces it, which the
CLI then replays and the library then loads — record → edit → replay, one
artifact, three front-ends. This is the Smalltalk/Lisp "the file and the REPL
are the same evaluator" invariant, carried to its end.

## What a "spec" becomes

A run spec stops being a distinct artifact and becomes **a reusable prefix of
config directives** — the leading lines of a program, factored out and named.

```bash
nb --spec headless < turns.jsonl    # prepend headless's config directives, then run the turns
```

`Extends: headless` means "prepend that preset's directives." Reuse survives;
the artificial spec-file / seed-file / prompt-arg boundaries dissolve, because
all three are the same directive schema. The relationship is
`spec : program :: a function's default arguments : a script`.

`nb -p "task"` (the yolo single-prompt form, see composable-CLI) is now
definable precisely: a one-directive program — a single pending `user` turn
under the default envelope.

## The boundary: construction is in, composition is out

This is close enough to a workflow format that the line must be pre-committed,
because someone will ask "why can't turn 4 reference turn 2's output?"

**A directive may construct and configure. No directive may consume a prior
turn's *output* as its input.** Config sets state; turn directives append
conversation; the model is the only thing that reads prior turns. The bright
line:

- **Fixed succession + configuration** (a set list of turns, provider set
  here, system set there) → a conversation-program. *In.*
- **Dataflow or control flow** (extract the JSON from turn 2 and interpolate
  it into turn 4; branch on a result; loop until done) → the host shell or C#
  between calls. *Out — by design, per composable-CLI's anti-goal.*

The test is one question: *does the next directive's text depend on a previous
directive's output?* No → it's a program. Yes → it's composition, and the
composition language is always the host's (bash, C#, a human at the REPL),
never a directive nb interprets. The worked example is pure construction — it
never crosses the line, which is exactly why it collapses to one invocation.

## Why this is not a DSL

The reflex objection — "composable-CLI forbade inventing a composition
language" — is answered on the strongest possible ground: **an ordered list of
`{role, content}` turns is the OpenAI/Anthropic `messages` array**, the single
most common LLM data shape in existence, that every coding model has seen
millions of times. This design invents no syntax; it adopts the industry
-standard conversation format and adds exactly two things it lacks:

1. **config rows** interleaved with turns, and
2. **the run-convention** (an unanswered final `user` turn executes).

A model authoring one of these is writing the format it is *most* fluent in.
This is the "in-distribution is a design criterion" principle
(composable-CLI, Design lineage) satisfied maximally: the program format has
the largest possible corpus, and the only novelty is a run-semantics
convention, not a language.

## Open decision

**Header-config vs anywhere-config.** Does a config directive appear only in a
leading header, or anywhere in the stream (enabling mid-program provider swap
— cheap model for one turn, expensive for the next)?

- The worked example needs only the header form.
- Anywhere-config is nearly free once config is an event, stays hermetic (one
  document = one deterministic, replayable program — *not* the hidden
  cross-invocation mutation Pillar 3 deletes), and buys the multi-model case
  for nothing.
- Cost: ordering becomes load-bearing ("when did I set the provider").
  Mitigation: `--resolve` prints the effective envelope at each run point.

Recommendation: **anywhere-config**, with `--resolve` as the ordering
inspector. Revisit only if the ordering semantics prove confusing in practice.

## Consequences for the other plans

- **`plans/transcript-schema.md` moves to the center of the system.** It is no
  longer "the output/seed format"; it is the program representation for output,
  seeds, tasks, `/save`, hooks, *and* the library. It must gain **config
  directives** (`config`, `system` as first-class events) and document the
  **run-convention** (pending-tail execution) as the semantics, not just the
  syntax. This raises the stakes on ratifying that schema first — it is now the
  load-bearing artifact of the entire reorientation.
- **`plans/composable-cli-reorientation.md` Pillar 1 is reframed.** "Run spec"
  is a reusable directive-prefix, not a separate document type. The spec schema
  and the transcript schema are one schema viewed in two regions (config
  prefix vs turn stream). The `Task` field is subsumed: it was an attempt to
  let a spec "carry the work," which is just "the program includes its pending
  turn."
- **The library facade (Pillar 5) gains the `Nb.Program()` builder** shown
  above as its primary surface, with `Nb.RunAsync(spec, task)` as sugar for the
  one-config-prefix-plus-one-pending-turn case.

## Related documents

- `plans/transcript-schema.md` — the bytecode this note makes central; must
  absorb config directives and the run-convention.
- `plans/composable-cli-reorientation.md` — parent; Pillar 1 (specs) and
  Pillar 5 (facade) are reframed here; the anti-goal boundary is honored.
