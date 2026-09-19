---
kind: bug
title: '`ResultEvent.DurationMs` is in the schema and the writer, and nothing ever sets it'
created: 2026-09-17
updated: 2026-09-19
status: current
state: fixed
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

## Fix (2026-09-19)

Fixed, and **scope changed by the owner's reframe**: the ask is "correct time spent
waiting on a provider, for whatever reason, inclusive of waits and retries", as a single
bucket. So the trailer gained two raw fields, not one:

- `duration_ms` — wall time for the program, from the first dispatched directive to
  trailer construction, excluding engine startup, as this report proposed.
- `provider_ms` — of that, the time blocked on a provider: inference, adaptive pacing,
  retry backoff, every retry attempt, and the oracle's side call.

**The `tool_ms` split this report floated is dropped, deliberately.** `duration_ms -
provider_ms` already answers the question a split was for, without nb having to
decompose anything or decide what counts as "tool time". The consumer subtracts.

The reasoning for one bucket rather than a breakdown: a consumer cannot act on *why* a
provider was slow. A large reasoning model taking ninety seconds and an overloaded
gateway are the same non-actionable fact, and separating them would be noise with a
maintenance cost. What *is* actionable is the complement — a slow tool call or an
expensive query — and that falls out of the subtraction.

## Where the measurement lives

Not in `Nb.RunAsync` as sketched, for `provider_ms`: `RetryingChatClient` is a
`DelegatingChatClient` installed at a single site (`ProviderManager.cs:149`) around every
client nb builds, and every wait the run pays — `PaceAsync`, the inner call, the backoff
`Task.Delay` — happens inside it. It is the only place that can see all of it. It also
catches the oracle for free, since `SideCallAsync` takes a client from the same path.

The accumulator (`nb.Core/ProviderTime.cs`) is scoped to `ProviderManager`, which is
built once per run, so a mid-program provider swap lands in the same total without a
parameter threaded through the evaluator. `duration_ms` is a plain stopwatch in
`Nb.RunAsync` as proposed.

Two things had to change to make the number honest:

- **`RetryingChatClient.Wrap` now always wraps.** It used to hand back the inner client
  untouched when retry and pacing were both off, which would have left `provider_ms`
  reading `0` rather than "unmeasured" — a trailer field silently wrong about itself,
  which is the failure this cluster keeps filing. `MaxRetriesZero_ReturnsTheClientUntouched`
  asserted that identity and was rewritten as `MaxRetriesZero_DoesNotRetry`, which
  asserts the contract that actually matters.
- **Streaming is timed inside `MoveNextAsync` only**, not around the enumerator: the
  caller does its own work between yields, so timing the loop would make `provider_ms`
  grow with how slowly nb renders. Moot once streaming is removed (TODO.md, "Chat-era
  surface cull").

## Test

Unit coverage is in `RetryBudgetAccountingTests` — provider time is charged with retry
disabled (proving the always-wrap), and includes backoff and pacing.

Eval (`evals/run.sh`): `duration_ms > 0`; `0 <= provider_ms <= duration_ms` (a
`provider_ms` above `duration_ms` would mean the accumulator double-counts waits); and
the one that proves the pair is worth having — a Mock run whose scripted `bash` call
sleeps a second shows that second in `duration_ms - provider_ms`, because Mock itself
answers instantly.
