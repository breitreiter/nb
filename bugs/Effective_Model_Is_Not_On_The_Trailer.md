---
kind: bug
title: 'The result trailer records the provider that answered but not the model, so a model sweep can be mis-attributed'
created: 2026-09-05
updated: 2026-09-05
status: current
state: open
severity: medium
cluster: provider-truthfulness
---

# The result trailer records the provider that answered but not the model

Status: Open (2026-09-05) — filed from the cluster-2 work, where
[`Failed_Provider_Directive_Silently_Substitutes.md`](Failed_Provider_Directive_Silently_Substitutes.md)
put `provider` on the trailer for exactly this reason and stopped one field short.
Against `0f24e6f`.

## Symptom

The run-level trailer names the provider entry that actually answered, and the harness
worn, and how many calls were denied:

```json
{"type":"result","exit_reason":"ok","turns":3,"tool_calls":2,"provider":"LocalCoder","harness":"codex","denied":0}
```

There is no `model`. A corpus of runs therefore records *which entry* answered but not
*what it was*, and a sweep across models is the common shape — the whole point of
`provider` + `model` being separate directives is that one entry serves many models:

```
provider LocalCoder
model qwen3-coder-next
run <task>
```

Nothing in the transcript distinguishes that run's output from the same program with
`model glm-4.5-air`. Both trailers read `"provider":"LocalCoder"`.

## Why this is the same defect as the provider one, not a nice-to-have

`RunResult.Provider` carries a doc comment that states the principle:

> The provider entry that **actually answered** — the effective one, not the one the
> program requested. […] exists so a corpus of runs cannot be mis-attributed to a
> provider that never ran.

Every clause of that applies verbatim to the model, and the effective model diverges
from the requested one in more ways than the provider does:

- **No `model` directive.** The effective model is the entry's `Model` config field,
  which is not in the program text at all. A program that never says `model` still runs
  *some* model, and the transcript does not say which.
- **No `Model` field either.** The provider plugin applies its own hard-coded default —
  `"claude-sonnet-4-6"` (`AnthropicProvider.cs:25`), `"gpt-4o-mini"`
  (`OpenAIProvider.cs:25`), `"gemini-2.0-flash-exp"` (`GeminiProvider.cs:25`). These are
  *inside the plugin*, so the answer isn't in the program or the config.
- **The config moved.** `appsettings.json` is not part of the transcript, so a corpus
  gathered over a week that spans an edit to an entry's `Model` is silently
  heterogeneous. `--seed` replays make this worse, since the premise transcript is
  pinned but the model is not.
- **A `provider` directive drops the model with it.** Already recorded in the sibling
  report's Notes: when the requested provider fails to build, the requested *model* goes
  too. That report fixed the abort; the trailer still would not have shown which model
  a successful-but-substituted run used.

Severity medium rather than low because the failure mode is the one this repo keeps
finding worst: **the number is scoreable and wrong about its own label.** A benchmark
sweeping model names produces per-model results whose model attribution comes from the
harness's *intent* rather than from the run, and nothing in the output contradicts a
mislabel.

## Mechanism

`TranscriptSerializer.WriteResultBody` (`:165`) writes `provider` and `harness` and no
model. `TranscriptMapper.ResultTrailer` (`:89`) takes `provider`, `harness`,
`deniedCount` and no model. `Nb.RunAsync` fills the provider from
`runtime.Conversation.GetCurrentProvider()` (`Nb.cs:96`); `ConversationManager` exposes
`GetCurrentProvider()` (`:158`) and **has no `GetCurrentModel()`**.

`ProgramEvaluator` does track `Model` (`:40`), set by a `ModelEvent`. That is *not* the
field to plumb through: it is the model the program **requested**, and it is null in
exactly the cases above where the effective model is most surprising. Copying it to the
trailer would reproduce the defect that `Failed_Provider_Directive_Silently_Substitutes`
was filed about — a trailer that records intent and reads as observation.

## Fix

**The effective model is already available and does not need plumbing from config.**
Microsoft.Extensions.AI exposes it on the client itself:

```csharp
client.GetService<ChatClientMetadata>()?.DefaultModelId
```

Checked against the shipped assemblies rather than assumed — both SDK families report
it, including the value that came from the *plugin's own* hard-coded fallback:

| client | `ProviderName` | `DefaultModelId` |
|---|---|---|
| `OpenAI.Chat.ChatClient(…).AsIChatClient()` | `openai` | `gpt-4o-mini` |
| `AnthropicClient(…).AsIChatClient("claude-sonnet-5")` | `anthropic` | `claude-sonnet-5` |

That is the right source precisely because it is downstream of every fallback: directive,
then entry `Model`, then the plugin's default, all collapsed into what the client will
actually send. Reading the config would only reproduce the first two.

Sketch: `ConversationManager.GetCurrentModel()` alongside `GetCurrentProvider()`, read
off the live `IChatClient`; `RunResult.Model` mirroring `RunResult.Provider` with the
same doc comment; `model` written next to `provider` in `WriteResultBody`; the field
omitted when null rather than written empty, matching how `harness` and `denied` behave.

**One wrinkle worth knowing before starting.** `MockProvider.GetService` returns `null`
unconditionally (`MockProvider.cs:148`) even though the class does expose a
`ChatClientMetadata` property (`:43`, model id `"mock-model"`). Mock drives nearly every
test and every eval, so as written the field would be absent from almost all of them.
Fix Mock to return its own metadata — it is a one-line change and it makes Mock honest
about a contract it already half-implements — rather than special-casing the reader.

Also decide: whether `--resolve` should print the effective model. It already prints the
wire surface and (per the accepted boundary plan) is to print egress endpoints; the
model is the same kind of "what will actually happen" fact, and `--resolve` can answer it
without spending a token.

## Test

`evals/run.sh` already asserts the sibling field —
`run_prog_stdout_contains "trailer: records the provider that answered" '"provider":"Mock"'`
(`:520`) — so the model assertion is one more line in the same shape once Mock reports
metadata. The unit-level case that matters is the one the program never states: a program
with **no `model` directive** whose entry has no `Model` field, asserting the trailer
carries the plugin's default rather than nothing. That is the mis-attribution this report
is about, and it is the assertion a config-reading implementation would fail.
