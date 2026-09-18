---
kind: bug
title: '`assistant_json` is defined, documented, and serialised, and nothing ever constructs one'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: low
cluster: schema-vs-dispatch
---

# `assistant_json` is defined, documented, and serialised, and nothing ever constructs one

Status: Open (2026-09-17) — filed from proctor, the sidecar experiment manager that
drives nb as a subprocess and reads the JSONL transcript. proctor's `answer_json` check
was written expecting this event and had to re-parse the fence itself instead. Against
`e34e762`.

## Symptom

A response whose final message is a ```` ```json ```` fence produces `assistant_text`
and `result` — never `assistant_json`:

```
$ printf 'run MOCK:response=```json \\\n{"a":1} \\\n```\n' | bin/Debug/net10.0/nb --config evals/test-appsettings.json --output jsonl -
{"type":"user","turn":0,"text":"MOCK:response=```json\n{\"a\":1}\n```"}
{"type":"assistant_text","turn":1,"text":"```json\n{\"a\":1}\n```"}
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":10,"output":5,"total":15},"turns":1,"tool_calls":0,"provider":"Mock"}
```

## Mechanism

`AssistantJsonEvent` is a complete, working type with nothing upstream that produces it.

- Declared and documented as the intended convenience
  (`nb.Core/Transcript/TranscriptEvent.cs:126-130`): *"Enrichment: the parsed final
  ```` ```json ```` fence, a convenience over the canonical `AssistantTextEvent`."*
- The writer handles it (`nb.Core/Transcript/TranscriptSerializer.cs:88-91`) and the
  reader parses it back (`nb.Core/Transcript/TranscriptSerializer.cs:274`).
- `ProgramEvaluator` explicitly lists it as output-only and moves on
  (`nb.Core/ProgramEvaluator.cs:174`): `// ThinkingEvent / AssistantJsonEvent /
  ResultEvent: output-only, ignored on input.`
- `docs/conversation-program-cli.md:528` documents `assistant_json` (`value`) in the
  trailer/event field list, alongside `assistant_text`'s `text`.

`grep -rn "new AssistantJsonEvent"` across `nb.Core` finds exactly one hit, and it's the
reader's own deserialisation path (`TranscriptSerializer.cs:274`, `ReadNode(root,
"value")`) — which only fires if an `assistant_json` line already exists on disk to
parse. Nothing in the live evaluation path (`ProgramEvaluator`, `ConversationManager`,
`TranscriptMapper.FromHistory`) ever detects a trailing fence in an assistant message and
emits one. The comment at `ProgramEvaluator.cs:174` reads as documentation of a finished
feature; it documents an unimplemented one instead.

## Why it matters

The type exists specifically so a consumer doesn't have to re-parse the fence itself —
that's the "convenience" in its own doc comment. As written, every consumer that wants
the parsed JSON (proctor's `answer_json` check among them) gets no `assistant_json`
event ever, on any provider, and has to extract and parse the fence out of
`assistant_text` by hand — exactly the work the type was meant to remove. It is silent:
there's no error, no warning, just an event type that is fully round-trippable and never
appears.

## Fix

Two honest options, not "the fix is to implement it" by default:

1. **Emit it.** After a final `AssistantTextEvent` for a run, check whether the text ends
   in a ```` ```json ... ``` ```` fence that parses as JSON; if so, append an
   `AssistantJsonEvent` with the parsed `Value` right after it. The natural site is
   `TranscriptMapper.FromHistory` (`nb.Core/Transcript/TranscriptMapper.cs`), which is
   already responsible for turning the live conversation into transcript events —
   consistent with `AssistantJsonEvent` being enrichment, ignored on seed-load
   (`TranscriptEvent.cs:126`), the same status `ResultEvent` has.
2. **Remove it from the schema doc and the type list** if the convenience isn't worth
   building — but the type, its writer/reader round-trip, and the golden test
   (`nb.Tests/TranscriptSerializerTests.cs:271`) all currently assert it's a real,
   supported event, so removal is a documented behaviour change, not a no-op.

Given a real consumer (proctor's `answer_json` check) already wants this, (1) is the
useful default; a program that never emits a trailing JSON fence sees no change.

## Test

Unit: extend `TranscriptSerializerTests` (or add a `ProgramEvaluator`/`Nb.RunAsync`
test) with a Mock run whose scripted response ends in a parseable ```` ```json ````
fence, asserting the resulting event list contains an `AssistantJsonEvent` with the
correct `Value` immediately after the `AssistantTextEvent`, and a second case — a
response with no fence, or a fence that fails to parse — asserting no `AssistantJsonEvent`
is added.
