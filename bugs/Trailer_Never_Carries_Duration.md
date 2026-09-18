---
kind: bug
title: '`ResultEvent.DurationMs` is in the schema and the writer, and nothing ever sets it'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: medium
cluster: schema-vs-dispatch
---

# `ResultEvent.DurationMs` is in the schema and the writer, and nothing ever sets it

Status: Open (2026-09-17) — filed from proctor, the sidecar experiment manager that
drives nb as a subprocess (`nb --output jsonl --config <cfg> program.nb`) and reads the
JSONL transcript. Against `e34e762`.

## Symptom

Every trailer lacks `duration_ms`, on every provider, always:

```
$ printf 'run MOCK:response=hi\n' | bin/Debug/net10.0/nb --config evals/test-appsettings.json --output jsonl -
{"type":"user","turn":0,"text":"MOCK:response=hi"}
{"type":"assistant_text","turn":1,"text":"hi"}
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":10,"output":5,"total":15},"turns":1,"tool_calls":0,"provider":"Mock"}
```

## Mechanism

The field is fully wired on the writer and reader sides and wired nowhere else.

- `ResultEvent.DurationMs` is declared (`nb.Core/Transcript/TranscriptEvent.cs:292`):
  `public long? DurationMs { get; init; }`.
- `TranscriptSerializer.WriteResultBody` emits it right after `tool_calls`, before
  `provider` (`nb.Core/Transcript/TranscriptSerializer.cs:190`):
  `if (r.DurationMs is { } d) w.WriteNumber("duration_ms", d);`
- The reader mirrors it on parse (`nb.Core/Transcript/TranscriptSerializer.cs:308`):
  `DurationMs = GetLong(root, "duration_ms"),`

But `grep DurationMs` across `nb.Core` turns up only those two sites plus the golden
fixture in `nb.Tests/TranscriptSerializerTests.cs:291`, which hand-sets `DurationMs =
4120` to exercise the writer — nothing in the engine path constructs a `ResultEvent`
with it set. `TranscriptMapper.ResultTrailer` (`nb.Core/Transcript/TranscriptMapper.cs:98`)
takes `exitReason`, `usage`, `harness`, `deniedCount`, `provider`, `oracleTurns`,
`oracleVerdict` — no duration parameter to pass one through. `RunResult`
(`nb.Core/Facade/RunResult.cs`) has no duration field either, so the gap is the same on
the library surface, not just the CLI's. The `if (r.DurationMs is { } d)` guard means the
key is silently omitted rather than written as `null` or `0`, so a consumer sees no
signal that the field was ever meant to exist.

## Why it matters

A consumer that wants wall time for a run — proctor measuring per-task latency across a
model sweep, for instance — has nothing in the transcript to read and must time the `nb`
subprocess itself. That measurement includes nb's own startup (config load, provider
plugin discovery, MCP connect attempts — order 2s observed locally) on top of the actual
conversation, and it cannot be decomposed into model time versus tool time. The schema
already anticipates exactly this: `DurationMs` sits on `ResultEvent` beside `Usage`,
`Turns`, and `ToolCalls` — the same trailer that already answers "how much did this run
cost" for tokens has no answer for "how long did it take."

## Fix

Set `DurationMs` in `Nb.RunAsync` (`nb.Core/Facade/Nb.cs:89-101`, where `RunResult` is
assembled) to the wall-clock span from the first `RunEvent`'s dispatch
(`ProgramEvaluator.cs:167-172`, where `_conversation.RunAsync` is awaited) to trailer
construction. That covers model latency across every `run` in the program but not
directives that do no I/O (`provider`, `budget`, …), which is the right boundary — it
answers "how long did inference take," not "how long did nb take to start."

Worth deciding separately: whether tool time inside a run (`bash`, MCP calls) should be
broken out as its own `tool_ms` rather than folded into the same number. Folded together,
`duration_ms` conflates "the model was slow" with "the tool was slow," which is the same
kind of conflation `provider`/`model` on the trailer were added to resolve for
attribution. A first cut can ship `duration_ms` alone and add `tool_ms` once there is a
concrete consumer that needs the split.

## Test

Unit: `TranscriptSerializerTests` already round-trips a `ResultEvent` with `DurationMs`
set (`:291`) — that direction is proven. The missing case is at `ProgramEvaluator`/`Nb`
level: a Mock run (deterministic, no real latency to flake on) whose trailer has a
non-null `duration_ms` greater than zero, and — if `tool_ms` is added — a run with a
scripted `bash` call whose `tool_ms` is less than its `duration_ms`.
