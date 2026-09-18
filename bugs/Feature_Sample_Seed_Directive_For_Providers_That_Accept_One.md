---
kind: bug
title: 'Feature: a `sample seed <n>` directive for providers that accept a sampling seed'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: low
cluster: feature-gap
---

# Feature: a `sample seed <n>` directive for providers that accept a sampling seed

Status: Open (2026-09-17) — filed from proctor (`proctor/project/todo.md`, "Candidates
for nb, not proctor"; `learnings/prior-art.md` asks for "a `budget`-style directive for a
deterministic random seed"). Against `e34e762`.

## What is wanted

A run-level directive that sets the provider request seed for subsequent runs:

```
sample seed 42
run MOCK:response=hi
```

On the wire it is a config event in `budget`'s key/value shape
(`{"type":"sample","key":"seed","value":42}`), and the trailer echoes `"seed":42` when
one was set. When the active provider cannot honour it, the run carries a warning in the
harness-costume style (`ProgramEvaluator.cs:269`, `"harness 'x' does not reproduce — …"`):
`provider 'Sonnet' does not reproduce — seed: Anthropic accepts no sampling seed`.

**Why not `seed <n>`.** `--seed <file>` is already the CLI flag that prepends a transcript
as premise history (`Program.cs:70`, `:212`; `docs/conversation-program-cli.md:56`;
CLAUDE.md:102 "continuity is explicit via `--seed`"). A program directive `seed 42` next to
`nb - --seed turn1.jsonl` would give one word two meanings. `sample` names the concern
(sampling parameters) the way `budget` names resources, and leaves room for
`sample temperature 0` later without a third verb.

## Why

proctor's report has a reproducibility block (pinned tool version, model snapshot, evals
sha). A seed belongs in it only where the provider honours one — OpenAI-compatible APIs
and some local servers do; Anthropic does not. With the seed on the wire and the ignore
warning on the run, the block is honest per arm instead of asserting determinism that
the transcript cannot back.

## Additive guarantee

A program with no `sample` directive builds `ChatOptions` exactly as today
(`ConversationManager.cs:377-382`, `Seed` left null); no event, no trailer key, no
warning. Seed-load: `sample` is a directive like `budget`, replayed as such.

## Where it lands

- `ProgramParser.cs:69-71` — `case "budget"` → `ParseBudget` (`:233-240`) is the
  template; add `case "sample"` → `SampleEvent { Key, Value }` beside `BudgetEvent`
  (`TranscriptEvent.cs:277-282`). Serializer: writer/reader cases beside `"budget"`
  (`TranscriptSerializer.cs:298`).
- `ProgramEvaluator.cs:160` — `case BudgetEvent` → `ApplyBudget` (`:346`); a
  `case SampleEvent` calls `_conversation.SetSeed(long?)`, a setter beside
  `SetTokenBudget` (`ConversationManager.cs:143`).
- `ConversationManager.cs:377-382` — `ChatOptions { Temperature = _temperature, … }`;
  `Seed = _seed` joins it. `Microsoft.Extensions.AI.ChatOptions.Seed` (`long?`) exists in
  the referenced 10.5.0 abstractions (`nb.Providers.Abstractions.csproj:21`) and the
  OpenAI adapters (`OpenAIProvider.cs:32`, `LocalLlmProvider.cs:43-45`) forward it; the
  Anthropic adapter (`AnthropicProvider.cs:41`) drops it. No provider code changes.
- Trailer: `ResultEvent.Seed` (`long?`), written after `harness`, omitted when null
  (`TranscriptSerializer.cs:175-195`); `TranscriptMapper.ResultTrailer` (`:98`) takes it.
- `docs/conversation-program-cli.md:174-177` — the directive table; `:536` the trailer.

## Test

Unit: `DirectiveShapeTests` — `sample seed 42` parses to `SampleEvent{seed,42}`;
`sample seed x` and `sample foo 1` are parse errors like `budget`'s. `MockChatClient`
(`MockProvider.cs:52-54`, receives `ChatOptions`) records `options.Seed` so a test can
assert `42` reached the client. Eval (`evals/run.sh`): a program with `sample seed 42`
yields `select(.type=="result").seed` of `42`; the plain Mock program has no `seed` key.

## Open decisions

- **Who says a provider ignores it.** A capability flag on `IChatClientProvider`
  (out-of-tree plugins must be able to declare it) versus a fixed list in nb.Core. The
  flag is the honest one; the list ships first.
- The oracle side-call (`OracleResolver.cs:89`, its own `ChatOptions`) is not seeded; the
  judge's determinism is `evals/oracle-bench/`'s question.
