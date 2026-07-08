---
kind: plan
title: The transcript schema — nb's one wire format for output, seeds, export, and hooks
created: 2026-07-07
updated: 2026-07-07
status: current
state: exploring
touches:
  files:
    - ConversationManager.cs
    - Program.cs
    - Utilities/MarkdownRenderer.cs
  features: [headless, seeds, statelessness, output, hooks, library-facade]
provenance:
  author: claude
---

# The transcript schema — nb's one wire format

## Why this plan exists

`plans/composable-cli-reorientation.md` asks a single JSON/JSONL schema to
carry four different loads:

1. **Output stream** — `--output jsonl` emits it (Pillar 4).
2. **Seed input** — `--seed file.jsonl` reads it back as premise (Pillar 3).
3. **Session export** — `/save` writes it (Pillar 3).
4. **Hook I/O** — the event piped to an external approval/tool command (the
   "Scripted extension points" section).

The umbrella plan's own words: *"the output contract doubles as the input
contract"* and *"one schema, four uses."* That symmetry is the load-bearing
claim of two whole pillars, and it is asserted, not designed — the umbrella
plan sketches event-type **names** (`tool_call`, `assistant_json`) but never
the record shapes, the round-trip rules, or where symmetry actually holds.

This is a keystone: if the schema can't be shared cleanly, Pillars 3, 4, and
5 each need their own format and the elegance argument collapses. It is also
the cheapest thing to get wrong on paper instead of inside
`ConversationManager.cs`'s 2126-line streaming/tool-merge tangle. So it gets
designed first, in isolation, against **real captured nb transcripts** — not
remembered structure.

This is a design/plan doc. No code.

**Scope note (2026-07-07):** `plans/conversation-program-evaluator.md`
promotes this schema from "output/seed format" to *the program
representation* for the whole system — output, seeds, tasks, `/save`, hooks,
and the library. Two additions this plan must absorb to serve that role,
flagged here and folded in on the next revision: **config directives**
(`config` with provider/model/output, and `system` as first-class events,
interleaved with turns) and the **run-convention** (a `user` turn with no
following `assistant` is *pending* and gets executed). The vocabulary and
round-trip rules below are unchanged by that; they gain two event types and a
documented evaluation semantics.

## What nb actually has today (verified against source + live runs)

The finding that shapes everything below: **nb already carries three
different representations of the same conversation, and they disagree.**

### Representation A — the history file (message-centric, model-facing)

`SaveConversationHistoryAsync` (`ConversationManager.cs:1663-1734`) writes a
JSON array of role-tagged messages. A real run — `nb --trust "run 'echo hi',
then list_dir '.', then tell me how many entries"` against a local model —
produced (system message elided):

```json
[
  { "Type": "UserChatMessage", "Content": "Use the bash tool to run 'echo hi', then use list_dir on '.', ..." },
  { "Type": "AssistantChatMessage", "Content": "",
    "ToolCalls": [
      { "CallId": "ncqT…", "Name": "bash",     "Arguments": {"description":"Run echo hi","command":"echo hi","timeout_seconds":10} },
      { "CallId": "yhn7…", "Name": "list_dir", "Arguments": {"path":"."} },
      { "CallId": "PEh1…", "Name": "bash",     "Arguments": {"description":"Count entries","command":"ls -1 | wc -l","timeout_seconds":10} }
    ] },
  { "Type": "ToolChatMessage", "Content": "",
    "ToolResults": [
      { "CallId": "ncqT…", "Result": "hi\n\n[exit code: 0]" },
      { "CallId": "yhn7…", "Result": "[dir]  prompts\n[file] appsettings.json\n… (55 lines)" },
      { "CallId": "PEh1…", "Result": "54\n\n[exit code: 0]" }
    ] },
  { "Type": "AssistantChatMessage", "Content": "hi\n\nThere are 54 entries in the current directory." }
]
```

Four structural facts this makes concrete, each of which constrains the schema:

- **Tool calls batch per assistant turn.** The model emitted *three* calls in
  **one** assistant message; they resolved as *three* results in **one** tool
  message. The round is `assistant(N calls) → tool(N results)`, not N
  independent call/result pairs. Any event-flattened stream must be able to
  **re-group** back into this shape or seeding produces malformed history.
- **`CallId` is the only join key** between a call and its result. It must
  survive every representation.
- **Tool results are opaque model-facing strings.** `bash` →
  `"hi\n\n[exit code: 0]"` — the exit code is *interpolated into the string*,
  not a field. This string is literally what goes back to the LLM; it is the
  irreducible, must-round-trip payload.
- **Assistant text and tool calls are mutually exclusive here** (`Content:""`
  when `ToolCalls` present), but the type system allows both — a message can
  carry prose *and* calls.

What A **drops**: reasoning (see C), token usage (never in history),
approval disposition, multi-part/image results (`.ToString()`-flattened —
the umbrella plan's acknowledged "images don't round-trip"), and any
per-tool structure (exit codes, entry counts) — all collapsed into the
result string.

### Representation B — the interactive display (event-ish, per-tool-shaped)

The same run rendered to the terminal as:

```
• bash: echo hi
✓ exit 0
• list_dir: .
  → 55 entries
• bash: ls -1 | wc -l
✓ exit 0
```

This representation **knows things A threw away**: that bash exited 0, that
list_dir returned 55 entries (note: *55* — the display counted before the
model's own `ls | wc -l` said *54*, because the lock file existed at display
time; the two counts legitimately differ). These structured facts exist only
as ephemeral `AnsiConsole.MarkupLine` calls scattered across the `Handle*`
methods (anchored exhaustively in `plans/headless-machine-output.md`). They
are computed and thrown away.

### Representation C — reasoning, stripped before either

`<think>…</think>` blocks are regex-stripped (`ConversationManager.cs:44-54`)
before an assistant message enters history (`:782`). Reasoning reaches the
*display* live but is deliberately absent from what the model sees next turn.

### What's reachable but unplumbed

- **Usage/telemetry.** `response.Usage` exposes `InputTokenCount`,
  `OutputTokenCount`, `TotalTokenCount` (`ConversationManager.cs:928-932`,
  currently only in a debug dump). It is **per-response, not per-message** —
  a run-level fact, never a conversation turn.
- **Approval disposition.** Which branch approved a call (auto / preapproved /
  prompted / rejected) is known at the call site but recorded nowhere.

## The central tension, stated precisely

The umbrella plan's proposed output schema (from
`headless-machine-output.md`) is **event-centric and per-tool-shaped**:

```json
{"type":"tool_call","tool":"bash","input":"…","approved":"auto"}
{"type":"tool_result","tool":"bash","exit_code":0,"truncated":false}
```

The seed loader needs **message-centric, model-facing** data: it rebuilds
`List<ChatMessage>` with batched calls and the *opaque result strings* the
model must see.

These pull opposite ways:

| Axis | Output stream wants | Seed input needs |
|---|---|---|
| Granularity | one event per call, streamed as it happens | rounds re-grouped into messages |
| Tool result | structured fields (`exit_code`, `entries`) | the verbatim model-facing string |
| Reasoning | surfaced live (`thinking` events) | **absent** (stripped from model view) |
| Usage | a final telemetry event | not a message at all |
| Approval | disposition per call | irrelevant |
| Images | a slot, even if unsupported | the model-facing text stand-in |

So **perfect symmetry is false.** The honest resolution — and the core
proposal of this plan — is not "one identical schema both directions" but:

> **One event vocabulary. Output is a superset; seed-load reads the subset
> it needs and ignores the rest. The subset is guaranteed lossless for
> round-trip.**

That is still "one schema, four uses" in the way that matters — one set of
record types, one parser, one document to version — but it names the
asymmetry the umbrella plan glossed, and turns it into an explicit
**core / enrichment** layering instead of a latent contradiction.

## The design

### Event-centric wire, with a turn index for lossless re-grouping

Recommend the **event stream** as the canonical form (streamable, matches the
umbrella plan's Pillar 4 direction, and a message array is trivially derivable
from it — the reverse is not true for streaming). The one addition that makes
message reconstruction lossless is a **`turn` integer**: a monotonic counter
that increments once per assistant round. Every event of a round shares its
`turn`, so the loader re-batches all `tool_call`s of a turn into one assistant
message and all `tool_result`s into the following tool message — exactly
Representation A's shape — with zero guesswork.

Without `turn`, N batched calls are indistinguishable from N sequential
single-call turns, and seeding a parallel-call model diverges from what really
happened. `turn` is the whole trick; it costs one integer.

### The event types

Core (round-trips — seed-load reads these):

```jsonc
{"type":"user","turn":0,"text":"…"}                         // or "content":[…] multipart
{"type":"assistant_text","turn":1,"text":"hi\n\nThere are 54 entries…"}
{"type":"tool_call","turn":1,"id":"ncqT…","name":"bash",
    "arguments":{"command":"echo hi","description":"…","timeout_seconds":10}}
{"type":"tool_result","turn":1,"id":"ncqT…","output":"hi\n\n[exit code: 0]"}
```

Enrichment (output-only — emitted, **ignored on seed-load**):

```jsonc
{"type":"thinking","turn":1,"text":"…"}                     // reasoning; never re-fed to model
{"type":"tool_call",…,"approved":"auto"}                    // approved ∈ auto|preapproved|prompted|rejected
{"type":"tool_result",…,"result":{"exit_code":0,"truncated":false,"timed_out":false}}  // structured mirror of `output`
{"type":"assistant_json","turn":3,"value":{…}}              // parsed final fence; convenience
{"type":"result","turn":null,"exit_reason":"ok",
    "usage":{"input":812,"output":143,"total":955},
    "turns":2,"tool_calls":3,"duration_ms":4120}            // run-level trailer, never a message
```

Design decisions:

- **`output` (string) is mandatory on `tool_result`; `result` (object) is
  optional enrichment.** `output` is the exact model-facing payload — the only
  thing seed-load consumes, the thing that must round-trip byte-for-byte. The
  structured `result` block is Representation B's rescued knowledge, present on
  output for machine consumers, ignored on load. This single split resolves
  the "structured vs opaque" row of the tension table: **both, in one record,
  with a clear which-is-canonical rule.** A consumer wanting exit codes reads
  `result.exit_code`; the loader reads `output`; neither fights the other.
- **`user`/`assistant_text` use `text`, but reserve `content: [...]` for
  multipart.** A part is `{"kind":"text","text":…}` or
  `{"kind":"image","media_type":…,"data":…|"note":…}`. v1 emits `text` for the
  common case and MAY emit `content` when a turn is multimodal. Images still
  don't fully round-trip (carried over from the umbrella plan), but they now
  have a *named slot* with a text-`note` fallback (`"[Image loaded: x.png]"` —
  what `read_file` already synthesizes, `ConversationManager.cs:409`) rather
  than silently vanishing. This is strictly better than A's `.ToString()`.
- **`thinking` is output-only, full stop.** It reaches the stream (default on,
  `--no-thinking` to drop) because live reasoning is useful to a debugging
  human. Seed-load **must discard it** — re-feeding stripped reasoning would
  violate the exact invariant `StripThinkBlocks` enforces
  (`ConversationManager.cs:44-54`). This is the cleanest example of "output
  superset, input subset": same vocabulary, one type that only ever flows
  outward.
- **`result` (the trailer) is not a message.** `turn:null` marks run-level
  events. It carries the telemetry Pillar 4 promised (`response.Usage` is
  already there) and the exit-reason that the umbrella plan's exit-code
  contract needs. Seed-load ignores it. This is the natural home for the
  `{turns, tool_calls, input_tokens, output_tokens, duration_ms,
  exit_reason}` the umbrella plan specified.
- **`assistant_json` is pure convenience over `assistant_text`.** When the
  final answer is a lone ```` ```json ```` fence, nb parses it and emits both
  the parsed `value` and (recommended) the raw `assistant_text`, so a consumer
  picks its layer and seed-load always has the canonical text. It is never the
  *only* representation of a turn.
- **Exactly one JSON object per line; unknown fields ignored, unknown
  `type` skipped with a warning.** Forward-compat by construction — a v2
  emitter's new event types don't break a v1 loader.

### Why event-centric beats shipping the message array directly

A tempting shortcut: make the wire format *be* Representation A (the message
array), identical both directions, "truly symmetric." Rejected:

- Streaming output can't buffer a whole message array; it must emit as it goes.
  Events are inherently streamable; a message array is a post-hoc structure.
- The per-tool enrichment (exit codes, approval, timing) has nowhere natural to
  live in A's role-message shape without polluting the model-facing record.
- Hooks (use #4) want a *single event* on stdin (`one tool_call in, one
  verdict out`), not a growing message array.

The event stream serves all four uses; the message array serves only two. So
the event stream is canonical and the message array becomes a derived view
(what seed-load builds in memory, what `/save --history` could emit for humans).

## Round-trip contract: exactly where symmetry holds

The guarantee, stated so it can be tested:

> Take any nb run. Emit `--output jsonl` → `t.jsonl`. Feed `--seed t.jsonl`
> to a new run. The reconstructed `List<ChatMessage>` **equals** the original
> run's history (Representation A) **modulo the enrichment layer** (thinking,
> approval, structured `result`, trailer, images-beyond-note).

Holds losslessly: user text, assistant text, tool call names + arguments +
`CallId`, tool result `output` strings, turn grouping.

Deliberately dropped on the way back (and *why it's correct to drop*):

- **thinking** — must not re-enter model view (invariant).
- **structured `result` fields** — redundant; `output` already contains what
  the model saw.
- **usage/trailer** — not conversational state.
- **approval disposition** — a fact about the *prior* run's policy, not
  conversational content.
- **image bytes** — v1 limitation; `note` text stands in.
- **the `system` event** — the consuming run's spec owns the prompt; dropped
  by default and warned about (see "Ratified: the system prompt belongs to
  the spec"). `--seed-system` overrides.

This is the "images don't round-trip" caveat from the umbrella plan,
generalized into a **precise, enumerated lossy set** rather than a footnote —
so a consumer knows exactly what survives replay.

## Validation contract

Both parent plans promise fail-fast, model-fixable errors
(`--validate`, seed-load checks). This schema is where those live. Rules:

- **`tool_call`/`tool_result` pairing.** Every `tool_result.id` matches a
  prior `tool_call.id` in the same `turn`; every `tool_call` in a completed
  turn has a matching result. Error names the orphaned `id` and turn.
- **Turn monotonicity.** `turn` is non-decreasing; message events within a
  turn follow `assistant → tool` order. A `tool_result` with no preceding
  same-turn `assistant`+`tool_call` is rejected.
- **Completed-round requirement (v1).** Per the umbrella plan's Pillar 3, a
  seed must end on a completed round — the last event is not a dangling
  `tool_call` without its `tool_result`. (Mid-turn seeding is the umbrella
  plan's explicitly deferred feature; the schema *permits* the dangling shape
  so the format needn't change when it lands, but v1 seed-load rejects it.)
- **Required fields per type**, with errors that name the field and allowed
  values (same standard the spec schema sets in the umbrella plan): a
  `tool_result` missing `output`, a `tool_call` missing `name`/`id`, an event
  with unknown `type` (warn+skip, don't fail).
- **`assistant_json` needn't re-validate** against anything — it's advisory;
  the raw text is canonical.

## Ratified: the system prompt belongs to the spec, never the seed (2026-07-07)

Decision: **seed-load ignores any `system` event in the transcript.** The
active spec's prompt layers are the sole source of the system prompt
(honoring `rules/model-policy-in-prompt-layers.md`). This is deliberate: it
lets a caller feed a synthetic or recorded transcript as *conversational
premise* without entangling it with *who nb is this run*. The two are
orthogonal concerns and this keeps them that way — you can fabricate "here's
what already happened" without also having to reproduce, or accidentally
override, the prompt.

The hazard this creates, and must defuse: the system prompt is conventionally
the **first thing** in a transcript, so dropping it silently invites two
failure modes —

1. **Instructions smuggled into system message 1.** An author writes guidance
   into the seed's system message expecting it to take effect; it's silently
   discarded and the run misbehaves.
2. **Unexpected prompt.** An author feeds a "self-contained" transcript,
   doesn't realize its system message was dropped, and the run uses a
   different prompt than they pictured.

Guardrails — all required, so the silent drop never bites:

- **Loud on drop.** When a seed carries a `system` event that is being
  ignored, nb warns to stderr, naming the file and the fix:
  `seed t.jsonl carries a system message; ignoring it — the system prompt
  comes from the active spec (use --seed-system to honor the seed's, or put
  instructions in the spec's Prompt.Base).` `--validate`/`--resolve` report
  the same. This kills failure mode 1 outright: a prompt cannot be *quietly*
  smuggled through a seed — the attempt is always announced.
- **A prompt floor, so "missing" can't happen.** Statelessness (umbrella
  Pillar 3) plus "every field defaults sensibly" (Pillar 1) means the spec
  *always* yields a system prompt — the empty spec reproduces the built-in
  baseline. So failure mode 2 degrades to "a different prompt than expected"
  (which the warning surfaces), never "no prompt at all." The only route to
  an empty system prompt is setting one explicitly in a spec — a deliberate
  act, not an accident of seeding.
- **An explicit escape hatch with defined precedence.** `--seed-system`
  honors the seed's system message, *replacing* the spec's. Precedence:
  with the flag, seed-system wins; without it, the spec always wins and the
  seed's system message is dropped. For the rare case of replaying a
  transcript with its exact original prompt.
- **Division of labor, documented for authors.** A seed is *what happened*
  (conversational premise); a spec is *the envelope* (who nb is, which
  tools, which prompt). The system message is envelope, so it belongs in the
  spec's `Prompt.Base` — never hand-authored into a seed. The published
  seed-format docs must state this in the **first paragraph**, with
  `--seed-system` as a footnote, not a headline — the default path should
  teach the right mental model.

Consequence for the output side: `--output jsonl` still emits a `system`
event (export completeness, human debugging, `--seed-system` replay), but it
is understood as *documentation of the run that produced the transcript*, not
an instruction to any run that consumes it.

## Open decisions (flag before building)

1. **Does `--output jsonl` interleave `thinking` and `tool_result.output` in
   full, or cap sizes?** A 5 MB bash output as a JSON string is legal but
   ugly. Proposal: emit full by default (the seed needs it), offer
   `--max-output-bytes` that truncates *with a `truncated:true` marker* — but
   truncation breaks round-trip, so it must be opt-in and loud. Decide whether
   truncation belongs here or only in the porcelain path.
2. **One file or two for `/save`?** The umbrella plan's `/save` exports *both*
   a transcript (seed) and an effective run-spec. Those are different schemas
   (this one vs the spec schema). Confirm they're sibling files, not one
   envelope.
3. *(Ratified 2026-07-07 — see "Ratified: the system prompt belongs to the
   spec, never the seed" above. Seed-load ignores the transcript's `system`
   event; the spec owns the prompt; a loud stderr warning + a prompt floor +
   `--seed-system` escape hatch defuse the silent-drop footgun.)*
4. **Arguments fidelity.** History reconstruction already coerces JSON number
   arguments to raw strings on load (`ConversationManager.cs:1794`). Decide
   whether the schema preserves original JSON types (cleaner) or inherits that
   quirk. Preserving types is the right call for a public contract.
5. **Event `type` names.** `assistant_text` vs `assistant`; `tool_call` vs
   `call`. Lock the vocabulary once — it's the public surface. Recommend the
   verbose forms already in `headless-machine-output.md` (they read well in
   `jq` selectors: `select(.type=="tool_call")`).

## Relationship to the parent plans + phasing

This schema is a **prerequisite artifact** for the umbrella plan's Phases 3
and 4, and the umbrella plan already says the schema "is defined once, shared
between Phase 3 (seeds) and Phase 4 (output stream) — whichever lands first
brings the schema with it." This document *is* that definition; it should be
ratified before either phase starts code.

Concretely it feeds:

- **Umbrella Phase 3** (seeds): the seed loader implements the message-array
  reconstruction + validation contract above.
- **Umbrella Phase 4** (jsonl output): the emitter implements the event
  vocabulary + enrichment layer + `result` trailer.
- **Umbrella Phase 5** (facade): `TranscriptEvent` records *are* this schema
  as C# types (the umbrella plan's Pillar 5 isomorphism rule); `run.Events`
  is `IReadOnlyList<TranscriptEvent>`.
- **Umbrella "Scripted extension points"** (hooks): a single `tool_call` event
  is the hook's stdin; a `tool_result` or a verdict is its stdout — no new
  schema, exactly as that section claims.

Suggested build order for the schema work itself (small, ahead of Phase 3):

- **S0 — ratify the vocabulary.** This doc + the two open-decision locks (5,
  and 3) that change record shape. No code.
- **S1 — define the types + serializer.** `TranscriptEvent` hierarchy and a
  reader/writer, unit-tested against the two captured runs in this doc as
  golden fixtures (round-trip A → jsonl → A). This is the single highest-value
  artifact: it de-risks Phases 3+4 before either touches
  `ConversationManager`.
- **S2 — wire emit + load** behind the umbrella plan's phases.

## Worked example: the captured tool run as jsonl

The real run from "What nb actually has today," emitted under this schema
(enrichment shown; `turn` grouping makes the three calls one round):

```jsonl
{"type":"user","turn":0,"text":"Use the bash tool to run 'echo hi', then use list_dir on '.', then tell me how many entries. Keep it terse."}
{"type":"tool_call","turn":1,"id":"ncqT…","name":"bash","arguments":{"command":"echo hi","description":"Run echo hi","timeout_seconds":10},"approved":"auto"}
{"type":"tool_call","turn":1,"id":"yhn7…","name":"list_dir","arguments":{"path":"."},"approved":"auto"}
{"type":"tool_call","turn":1,"id":"PEh1…","name":"bash","arguments":{"command":"ls -1 | wc -l","description":"Count entries","timeout_seconds":10},"approved":"auto"}
{"type":"tool_result","turn":1,"id":"ncqT…","output":"hi\n\n[exit code: 0]","result":{"exit_code":0,"truncated":false}}
{"type":"tool_result","turn":1,"id":"yhn7…","output":"[dir]  prompts\n[file] appsettings.json\n…","result":{"entries":55}}
{"type":"tool_result","turn":1,"id":"PEh1…","output":"54\n\n[exit code: 0]","result":{"exit_code":0,"truncated":false}}
{"type":"assistant_text","turn":2,"text":"hi\n\nThere are 54 entries in the current directory."}
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":812,"output":143,"total":955},"turns":2,"tool_calls":3}
```

Seed-load of this file rebuilds exactly Representation A (system message from
the new spec, not the file — and had the file carried a `system` event, nb
would warn to stderr that it was ignored): user → assistant(3 calls) →
tool(3 results) → assistant(text). The `approved`, `result`, and trailer are
read and dropped. Byte-for-byte the model sees what it saw the first time.

## Related documents

- `plans/conversation-program-evaluator.md` — the thesis that makes this
  schema the system's center (the "bytecode"); source of the config-directive
  and run-convention additions in the scope note above.
- `plans/composable-cli-reorientation.md` — parent; Pillars 3/4/5 and the
  hooks section all consume this schema. This doc discharges its "define the
  transcript schema once" obligation.
- `plans/headless-machine-output.md` — supplies the output-side event names
  and the per-tool result-field inventory adopted into the enrichment layer.
- `rules/model-policy-in-prompt-layers.md` — constrains open-decision 3
  (system prompt stays spec-owned, not seed-smuggled).
