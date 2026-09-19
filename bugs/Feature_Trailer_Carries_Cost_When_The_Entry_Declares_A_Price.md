---
kind: bug
title: 'Feature: the result trailer carries `cost` when the provider entry declares a price'
created: 2026-09-17
updated: 2026-09-19
status: current
state: fixed
severity: low
cluster: provider-truthfulness
---

# Feature: the result trailer carries `cost` when the provider entry declares a price

Status: Open (2026-09-17) — filed from proctor
(`proctor/project/learnings/prior-art/promptfoo-checks.md`, "Candidates for nb, not
proctor"). Against `e34e762`.

## What is wanted

The trailer reports `usage{input,output,total,estimated?}` (`TranscriptSerializer.cs:178-186`)
and nothing about money. Two optional fields on a `ChatProviders` entry, named in the
entry's existing unit-suffixed style (`RetryBudgetSeconds`, `MaxContextTokens`):

```json
{ "Name": "Sonnet", "Provider": "Anthropic", "Model": "claude-sonnet-4-6",
  "InputPricePerMillionTokens": 3.0, "OutputPricePerMillionTokens": 15.0 }
```

When either is present the trailer gains `cost`, in USD (decided: the only currency any
price sheet nb's users read is quoted in; documenting it as USD costs nothing and avoids
a `currency` field), computed as `input × in/1e6 + output × out/1e6`, with a missing
side priced at zero:

```json
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":1200,"output":300,"total":1500},"turns":1,"tool_calls":0,"provider":"Sonnet","cost":0.0081}
```

A local entry (imp's `LocalLlm` ports) declares nothing, or `0` if it wants an explicit
`"cost":0` on the trailer.

## Why

A transcript should be attributable — provider, model, cost — without an external table.
proctor would otherwise keep a price list keyed by `provider`+`model` that drifts from the
config that actually ran: the config-not-in-the-transcript argument that put `provider`
on the trailer (`Failed_Provider_Directive_Silently_Substitutes.md`) and asks for `model`
(`Effective_Model_Is_Not_On_The_Trailer.md`). The entry already knows the endpoint, key
and model; the price belongs beside them, and the run that spent the tokens is the
moment to multiply.

## Additive guarantee

`cost` is emitted only when the entry declares a price. An entry without one — every
entry in `appsettings.example.json` today, and Mock, which drives every test and eval —
produces a trailer byte-identical to before. `ResultEvent` is enrichment, skipped on
seed-load (`TranscriptLoader.cs:124`, `IsCore`), so replay is unaffected.

## Interaction with `estimated: true`

When usage is nb's size estimate (`ConversationManager.cs:1092`), cost is estimated too.
Do **not** add a second flag: `cost` inherits `usage.estimated`, documented beside the
existing "don't bill from an estimated trailer" warning (`docs/conversation-program-cli.md:558`).

## Where it lands

- `ProviderConfig.cs` (`Providers/nb.Providers.Abstractions/`) — two key constants and a
  reader beside `Headers`/`HasRequired`, so out-of-tree providers share the parse.
- `ConversationManager.cs:118`/`:154` — where `_currentProviderName` is set; the price
  rides the same path (a `SwitchProvider` parameter, or `ProviderEntries.ReadAll`, `ProviderEntries.cs:44`).
- `Nb.cs:81-82` — usage is assembled; cost computed beside it and set on `RunResult`.
- `TranscriptEvent.cs:285` — `ResultEvent.Cost` (`double?`); `TranscriptMapper.ResultTrailer`
  (`:89`) takes it; `WriteResultBody` (`TranscriptSerializer.cs:175`) writes it after
  `provider`, omitted when null like `harness`; the reader at `:313` mirrors it.
- `docs/conversation-program-cli.md:536` — the trailer field list.

## Test

Unit: `TranscriptSerializerTests` — a `ResultEvent` with `Cost = null` round-trips with no
`cost` key; with a value it does. Eval (`evals/run.sh` asserts trailer fields —
`usage.total` at `:395`, `provider` at `:611`): a config whose Mock entry declares
`InputPricePerMillionTokens: 1000` yields `select(.type=="result").cost` of `0.01` from
Mock's 10 input tokens, and the existing default-config trailer eval has no `cost` key.

## Open decisions

- **Provider switches mid-program.** Usage sums across runs (`ConversationManager.cs:274`)
  and the trailer names only the last provider. Accumulate cost per round-trip at the
  price live at that moment (the shape `TotalUsage` already has) rather than multiplying
  the sum by the final entry's price.
- Cache-read and reasoning tokens are not on `UsageInfo`; they price as plain
  input/output. Fine for a first cut; say so in the docs.

## Fix (2026-09-19)

Implemented as specified, including the open decision the report flagged.

`InputPricePerMillionTokens` / `OutputPricePerMillionTokens` on a `ChatProviders` entry,
parsed by `ProviderConfig.Prices` in `nb.Providers.Abstractions` so an out-of-tree
provider shares the parse. USD, no currency field. `cost` is emitted only when an entry
declares a price, so every existing config — and Mock, which drives nearly every test and
eval — produces a byte-identical trailer.

**Provider switches: accumulated per round-trip at the price live at that moment**, the
option the report preferred, not the summed usage multiplied by the final entry's price.
`ConversationManager` charges at both usage sites (the tool-loop round-trip and the
oracle's side call) through a price lookup resolved lazily by entry label, so a
mid-program `provider` directive charges at the new entry's price without the
conversation needing to be told about the swap.

`cost` inherits `usage.estimated` with no second flag, as the report argued. Cache-read
and reasoning tokens are not on `UsageInfo` and price as plain input/output; that is
stated in the docs.

## Test

Eval: a default-config run has no `cost` key at all; a fixture whose Mock entry declares
1000/2000 per million yields `0.02` from Mock's 10 input and 5 output tokens.
