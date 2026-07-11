---
kind: plan
title: nb is an evaluator for conversation-programs
created: 2026-07-07
updated: 2026-07-10
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
the same bytecode. A "run" is nothing more than the evaluator reaching a
`run` directive.**

Everything the composable-CLI plan treats as a separate feature — run specs,
seeds, the task prompt, `/save`, the library facade — is a projection of this
one idea. Spec / seed / task are not artifacts; they are *regions* of a single
document.

## The program model

A conversation-program is an **ordered stream of directives**. Two kinds:

- **Config directives** (`provider`, `model`, `output`, `approval`, `mcp`) —
  set the envelope going forward. Order matters: a config directive governs
  every directive after it until overridden.
- **Turn directives** (`system`, `user`, `assistant`, `tool_call`,
  `tool_result`) — append a message to the conversation. Note `system` lives
  here, not in config: it is just a message with the system role, zero or more
  of them, wherever the author puts them. "The system prompt" as a singular,
  owned entity is a fiction — there are only system-role messages.

And one invocation directive:

- **`run` sends the accumulated conversation to the model**, appends the real
  response, and continues. It is the *only* directive that triggers
  inference; every other directive merely asserts state. `run` may carry an
  inline prompt — `run <text>` is sugar for `user <text>` then `run` — or
  stand bare to invoke on the state built so far. A program may `run` any
  number of times, with config directives between runs, which is how
  mid-stream reconfiguration (cheap model for one turn, expensive for the
  next) is expressed.

This makes the seed/script split fall out of a single bit: **a program with no
`run` is a seed** — pure fabricated state, nothing executes; **a program with
`run`s is a script.** `user`/`assistant` directives always assert history;
only `run` invokes. There is no implicit execution and no third concept —
construction, configuration, and invocation are all directives, and nothing
runs unless you say `run`. (An earlier draft used an implicit convention —
"a trailing unanswered `user` turn executes" — but that misfires on a seed
that legitimately *ends* on a user turn, firing an unwanted run. Explicit
`run` removes the ambiguity and is what maps to the REPL's enter key.)

## The worked example, as one program

The six-invocation flow above, written in the source syntax (below):

```
provider anthropic
model claude-sonnet-5
system you are a terse assistant
user fabricated turn 1
assistant fabricated answer 1
user fabricated turn 2
assistant fabricated answer 2
user fabricated turn 3
assistant fabricated answer 3
run the real prompt
```

```bash
resp=$(nb < flow.nb)      # one invocation; stdout is only the model response
```

Every line above `run` asserts state the caller constructed; `run` is the one
line that invokes the model, and (via the inline-prompt sugar) carries the
real prompt with it. The document is a near-verbatim transcription of the six
original steps. It desugars to the JSONL bytecode — same directives as typed
events, plus an explicit `run` event — which is what `--output jsonl` emits
and what round-trips.

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
      .Run("the real prompt");     // asserts a user turn, invokes, returns the response
  ```

  `.User()`/`.Assistant()` assert state; `.Run()` is the only call that
  invokes — the fluent form of the `run` directive.

- **REPL** — the same program typed live, and this is where `run` earns its
  keep: **a bare line typed at the prompt is `user <text>` + `run` — the enter
  key *is* the `run` directive.** `/provider` and `/system` are config
  directives; a new `/assistant "…"` (and `/user "…"`) command authors a turn
  *without* invoking — the affordance the REPL lacks today, and the bridge to
  hand-authored seeds. The REPL is simply the evaluator in interactive mode,
  with enter bound to `run`.

The rhyme is exact because all three emit the same bytecode. `/save` is the
proof: it exports a REPL session as the program that reproduces it, which the
CLI then replays and the library then loads — record → edit → replay, one
artifact, three front-ends. This is the Smalltalk/Lisp "the file and the REPL
are the same evaluator" invariant, carried to its end.

## What a "spec" becomes

A run spec stops being a distinct artifact and becomes **a reusable prefix of
config directives** — the leading lines of a program, factored out and named.

```bash
nb --spec headless < turns.nb    # prepend headless's config directives, then run the turns
```

`Extends: headless` means "prepend that preset's directives." Reuse survives;
the artificial spec-file / seed-file / prompt-arg boundaries dissolve, because
all three are the same directive schema. The relationship is
`spec : program :: a function's default arguments : a script`.

`nb -p "task"` (the yolo single-prompt form, see composable-CLI) is now
definable precisely: a one-directive program — `run task` under the default
envelope.

## The source syntax: line-oriented directives over the JSONL bytecode

The JSONL above is **bytecode** — canonical, complete, what `--output` emits,
what round-trips. Nobody hand-writes it (a multi-line prompt strangled with
`\n` escapes is miserable). The authoring surface is a **line-oriented source
syntax** that desugars to it — the "reasonable reply" form. The two are the
source→bytecode layering the thesis already implies; this section draws the
source layer.

The grammar is one rule: **a line is `<verb> <content>`. The first token is
always the verb; everything after the first space is content.** Verbs are the
directive set (`provider`, `model`, `system`, `output`, `mcp`, `user`,
`assistant`, `tool_call`, `tool_result`, `run`). This borrows the git-rebase-todo /
Dockerfile / markdown-transcript shape — verb-first, line-oriented, arg-to-end
-of-line — all large-corpus formats.

- **Collisions resolve by using the syntax, not by escaping it.** Because the
  first token is *definitionally* the verb, content that begins with a
  verb-word just names the verb again: `system system design is hard` →
  verb `system`, content `"system design is hard"`. No fences, no
  case-sensitivity rule, no ambiguity — position 1 is the verb, period. (The
  fence and case-sensitivity machinery of earlier drafts had no hand-author
  consumer and is dropped.)

- **Multi-line is strict: one logical line per turn.** A turn is a single
  logical line. To span physical lines by hand, use a trailing backslash
  continuation (`\`), the shell/Dockerfile convention a hand-author can
  remember to type. There is no block mode and no bare-continuation mode —
  every physical line either starts with a verb or continues the previous one
  via an explicit `\`. This keeps "position 1 is always a verb" true without
  exception, at the cost of making genuine multi-line content deliberate.

- **The escape for content the source can't hold is the bytecode**, not more
  source syntax. A program with exotic content (embedded transcripts,
  images, odd tool_result fields) is authored — or generated — as JSONL. The
  source layer is an *upward-lossy* convenience over a complete instruction
  set, exactly the assembler relationship.

**Companion requirement — paste support in UglyPrompt.** Strict multi-line
assumes backslashes, and *pasted* text never has them: paste a three-paragraph
prompt from a doc or another chat and every physical newline lands as a
bare line with no verb — a parse error per line. Hand-typing is fine (you add
the backslashes); paste is the real gap. So the interactive editor
(UglyPrompt) must grow **multi-line paste handling**: capture a bracketed
paste as one unit and fold it into the current turn's content (auto-continue
across its internal newlines) rather than treating each pasted line as a new
directive. Tracked in `TODO.md`; see also
`plans/UglyPrompt_Multi_Source_Completions.md`. This is a prerequisite for the
source syntax being pleasant with real-world pasted content, and it is the one
place strict multi-line pushes cost onto the editor instead of the format.

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
2. **an explicit `run` directive** that invokes the model on the accumulated
   state.

A model authoring one of these is writing the format it is *most* fluent in.
This is the "in-distribution is a design criterion" principle
(composable-CLI, Design lineage) satisfied maximally: the program format has
the largest possible corpus, and the only novelty is a run-semantics
convention, not a language.

## Header-config vs anywhere-config (decided: anywhere)

Where may a **config directive** (`provider`/`model`/`output`/`approval`/`mcp`
— *not* `system`, which is a message) appear: only in a leading header, or
anywhere in the stream?

This decision is envelope-config-only. `system` is a turn directive, so
mid-stream `system` needs no special ruling — it is just a system-role message
at that point in the conversation, exactly as `user`/`assistant` are messages
at their points. An earlier draft treated mid-stream `system` as the hard case;
that was the singular-system-prompt fiction talking. It isn't a case at all.

For envelope config:

- Anywhere-config stays hermetic (one document = one deterministic, replayable
  program — *not* the hidden cross-invocation mutation Pillar 3 deletes) and
  buys the multi-model case for nothing: cheap model for one `run`, expensive
  for the next, in one file. With `run` now explicit, config directives slot
  naturally between runs.
- Header-only creates a **cliff**: you'd handle "3 turns, one model" in a
  single document but be forced back to shell-chaining two invocations the
  moment you need "3 turns, two models" — reintroducing exactly the
  multi-invocation fragmentation this whole design collapses.
- Cost: ordering becomes load-bearing ("which provider is active at this
  `run`"). Mitigation: `--resolve` prints the effective envelope at each run
  point.

**Decided: anywhere-config**, with `--resolve` as the ordering inspector.
Revisit only if ordering semantics prove confusing in practice.

## The `mcp` directive: the tool surface is envelope config

The umbrella plan's spec carried `Tools.McpServers` ("kits lose their MCP
monopoly — a spec exposes servers directly"). When specs became a reusable
prefix of config directives, that field never got a verb. It gets one: **`mcp`
is a config directive that enables/disables MCP servers by name.**

- **Content is `+name` / `-name` tokens**, any number per line:
  `mcp +figma`, `mcp -built-in-tester`, `mcp +figma -built-in-tester`. The
  `+`/`-` shape deliberately rhymes with the existing `+kit` tokens. `mcp none`
  clears the set — the absolute reset for callers that want a known-empty
  surface regardless of preset.
- **Delta semantics, because presets are prefixes.** A preset's `mcp`
  directives establish a baseline; a program layered after it can add one
  server or drop one without knowing the preset's full set. Absolute-set
  semantics would force every program to restate its preset's servers.
  `--resolve` prints the effective server set at each `run` point, same as the
  rest of the envelope.
- **Names refer to servers defined in the config layers (`mcp.json`).** The
  directive toggles *exposure* of an already-defined server; it does not define
  servers inline (connection config stays in the config layers, per Pillar 2).
  An unknown name is a validation error that lists the known server names.
- **Baseline is empty, floor comes from the preset.** Same rule as the system
  message: a bare program (`nb < program.nb`, no preset) exposes **no** MCP
  servers; the default preset on the human path carries whatever `mcp`
  directives make interactive nb useful. Nothing is conjured behind the
  program.
- **Takes effect at the next `run`.** Like all envelope config, `mcp` asserts
  state for subsequent directives; the evaluator resolves connections lazily
  when a `run` needs them (today's connect-everything-at-startup becomes
  per-run resolution). Enabling for one `run` and disabling after is the
  least-privilege idiom: `mcp +figma` / `run …` / `mcp -figma`.
- **Exposure, not permission.** Enabling a server puts its tools on the
  model's tool list; the approval policy (`approval` directive, `alwaysAllow`)
  still governs each invocation, unchanged.

Open (flagged, not decided here): whether the spec's remaining `Tools` fields —
native-tool selection and the todo toggle — get a sibling `tools` verb or fold
into `mcp`'s pattern. Native tools have a different default (on, not off), so
they are not the same decision.

## The system message: no auto-injection; presets carry it; `@file` includes it

Because `system` is just a message, two questions the singular-prompt fiction
used to hide need explicit answers.

**Is a system message auto-injected when the program writes none? No.** There
is no magic injection into an arbitrary program. Instead the **default preset**
— the one resolved for the human / `-p` / bare-prompt path — carries the
`system` directive(s). The floor is provided explicitly-as-directives and is
visible under `--resolve`, not conjured behind the program:

- `nb -p "quick question"` → `[default preset's directives, incl. a system
  directive] + run "quick question"`. You get the nb persona because the preset
  *says so*.
- `nb < just-turns.nb` (no preset, no `system`) → **no system message.** Exactly
  what was written — which is what an eval harness needs: the thing under test
  isn't silently wrapped in nb's assistant persona.

This keeps the sound half of the old "presets own the prompt, nothing smuggles
one in" rule while dropping the fiction it rested on.

**Do we support `system @file`, or assume inline copy-paste? `@file` — and it
is not optional.** The base prompt lives in a file (`prompts/system.md`); a
preset cannot maintainably inline it as a literal (that copy would drift). Once
"presets carry system" meets "prompts live in files," presets *must* reference
files. Add that nobody hand-inlines a long system prompt, and `@file` wins on
necessity, not just ergonomics. Two properties keep it from being scope-creep:

- **It is a source-layer include that resolves to inline content in the
  bytecode.** `system @base.md` desugars, at load time, to a `system` event
  with the file's contents baked in. The JSONL that `--output` emits and that
  round-trips is therefore self-contained and hermetic — the reference lives
  only in the source you author in place, where the file is. This is the
  source→bytecode relationship again: `@file` is a `#include`, resolved away in
  the canonical form.
- **It is content-inclusion, not composition-with-dataflow.** `@file` pulls in
  *content*, never a prior `run`'s *output*, so it does not touch the
  anti-composition boundary — a preprocessor include, not a workflow.

Inline literal content stays valid; `@file` is sugar over it. Two sub-rules:
**path resolution** — relative to the program file, falling back to the
config-layer prompt dirs so presets can reference shipped prompts; and
**generality** — the include is orthogonal to which directive uses it, so it is
`@file` on *any* content directive (`user @question.md`, `assistant
@canned.md`), not a system-only feature. Aligns with
`plans/At_Mention_Files.md`.

## Consequences for the other plans

- **`plans/transcript-schema.md` moves to the center of the system.** It is no
  longer "the output/seed format"; it is the program representation for output,
  seeds, tasks, `/save`, hooks, *and* the library. It must gain **config
  directives** (`provider`/`model`/`output`/`approval`/`mcp` as first-class
  events),
  a **`system` message event** (a turn event, not config), and an explicit
  **`run` event** as the invocation semantics — not the implicit pending-tail
  convention. In a *program* (input), `run` marks where inference happens; in
  a *recorded* transcript (output), each past `run` shows up as the
  `assistant` result it produced — the source↔output symmetry. This raises the
  stakes on ratifying that schema first — it is now the load-bearing artifact
  of the entire reorientation.
- **`plans/transcript-schema.md`'s system-prompt ruling is reworked (done
  2026-07-09).** The old "seed drops the system message" decision — built on the
  singular-owned-prompt fiction — is replaced by "The system message and the
  prompt floor": `system` is a plain message that round-trips, nothing is
  dropped or injected, the floor is the default preset carrying the prompt
  layers as `system` directives (loaded only on the human/`-p` path, per the
  rc-file rule), and transparency (`--resolve`) replaces the drop-and-warn
  machinery, which is retired along with `--seed-system`.
- **`@file` includes rest on UglyPrompt at-sign completion — now largely
  built.** The mechanism landed in UglyPrompt 0.3.0 (pluggable
  `CompletionSource`, `@` trigger), and **0.4.0 added Tab-to-accept** — the
  actual fill-in-the-path UX that was the real gap (top-match-only: Tab commits
  the top candidate, Enter submits, disambiguate by typing more). The
  sync-`Lookup` worry is resolved *by design*: nb's `@` source should build its
  file index once and filter in-memory, so there is nothing to block on per
  keystroke (the doc's explicit adoption guidance). Framework is aligned too —
  0.4.0 moved to net10, matching nb, and it is published on NuGet. **nb has now
  been migrated to 0.4.0** — the pin is bumped and the `/` command and `+` kit
  hints are ported to `AddSource(...)` (the `+` source reads kits live), and the
  **`@` file *completion* source is wired and unit-tested** (`FileMentionSource`
  — lazy index built once from the launch dir, filtered in memory, skip-dirs
  honored, capped). What remains is the *consumption* half: resolving a typed
  `@path` into inline file content (the include semantics), which is downstream
  and not yet built. One minor known bug persists — the hint strip is clobbered
  when input wraps to a new row — relevant to long source-syntax lines but not
  blocking. The include's
  *semantics* (source-time resolution to inline bytecode) are independent of
  the editor and can land first.
- **`plans/composable-cli-reorientation.md` Pillar 1 is reframed.** "Run spec"
  is a reusable directive-prefix, not a separate document type. The spec schema
  and the transcript schema are one schema viewed in two regions (config
  prefix vs turn stream). The `Task` field is subsumed: it was an attempt to
  let a spec "carry the work," which is just "the program ends in a `run`."
- **The library facade (Pillar 5) gains the `Nb.Program()` builder** shown
  above as its primary surface, with `Nb.RunAsync(spec, task)` as sugar for the
  config-prefix-plus-single-`run` case.

## Related documents

- `plans/transcript-schema.md` — the bytecode this note makes central; must
  absorb config directives and the explicit `run` event.
- `plans/composable-cli-reorientation.md` — parent; Pillar 1 (specs) and
  Pillar 5 (facade) are reframed here; the anti-goal boundary is honored.
