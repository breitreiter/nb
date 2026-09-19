---
kind: bug
title: 'Feature: the doom-loop and pending-todo reminders carry a `source` tag on their `user` event'
created: 2026-09-17
updated: 2026-09-19
status: current
state: fixed
severity: low
cluster: feature-gap
---

# Feature: the doom-loop and pending-todo reminders carry a `source` tag on their `user` event

Status: Open (2026-09-17) — filed from proctor
(`proctor/project/learnings/prior-art/promptfoo-checks.md`, "Candidates for nb, not
proctor"), the sidecar grader being designed beside nb. Against `e34e762`.

## What is wanted

nb injects two reminders mid-turn as plain `user` messages: the doom-loop nudge
(`ConversationManager.cs:724-727`) and the pending-todos reminder (`:757-760`). On the
wire both are indistinguishable from a user turn the program wrote — `{"type":"user",
"turn":n,"text":"<system_reminder>You appear to be stuck…"}`. The ask is one enrichment
field each, in the slot the oracle already uses:

```json
{"type":"user","turn":4,"source":"loop","text":"<system_reminder>You appear to be stuck in a repetitive loop…"}
{"type":"user","turn":6,"source":"todo","text":"<system_reminder>You have pending todo items…"}
```

## Why

A proctor check like `loop_nudged: false` should count events, not match reminder
prose. The prose is tunable (`:724` already varies it on `_oracleAvailable`), so a regex
over it breaks on the next wording change and gives a silently wrong count. The oracle
solved this the same way: its answers are ordinary user turns on the wire *and* carry
`source: "oracle"` + `keys[]` (`docs/conversation-program-cli.md` §8, `user` row; §4.1).
Reminders are the other two things nb says in the user's voice that the program did not write.

## Additive guarantee

A program that never loops and never leaves a todo open emits no reminder, so its
transcript is byte-identical. `Source` is enrichment, ignored on seed-load
(`TranscriptLoader.cs:124`, `IsCore` keeps the event; `ToContents` never reads the field),
so a nudged transcript replays the reminder as the ordinary user message it already was —
the property `UserEvent.Source`'s doc comment (`TranscriptEvent.cs:47-53`) promises for
`"oracle"`. This extends the vocabulary, not the contract.

## Where it lands

- `ConversationManager.cs:220` — `_oracleAnswers` is a reference-keyed
  `Dictionary<AIChatMessage, OracleAnswer>` exposed as `OracleAnswers` (`:223`). Either
  generalise it to a `Dictionary<AIChatMessage, string> InjectedSources` (recording
  `"loop"` at `:727` and `"todo"` at `:760`) or add a sibling map; the oracle keeps its
  `keys`/`verdict` either way.
- `TranscriptMapper.cs:84-85` — the one place `Source` is set, from `oracleAnswers`;
  `FromHistory` (`:36`) grows a parameter or reads the generalised map.
- `Nb.cs:79` — the `FromHistory` call site. `Program.cs` mirrors it for the CLI path.
- `TranscriptSerializer.cs:66` and `:246` — already write and read `source` for any
  string. No change.
- `docs/conversation-program-cli.md:514` — the `user` row's enrichment list.

The turn-dump labels are already `"loop"` (`:730`) but `"todos"` (`:763`); the wire value
should be `"todo"`, singular, matching the tool name and `tools -todo`.

## Test

Unit: `TranscriptMapperTests` — a history containing a message recorded as a loop
reminder maps to a `UserEvent` with `Source == "loop"`, and a plain user message maps
with `Source == null`. Eval (`evals/run.sh`, which already asserts `source`/`keys` for the
oracle at `:152`): a Mock program driven into the doom loop yields
`[.[]|select(.type=="user" and .source=="loop")]|length` of `1`; the existing "no
directive, trailer unchanged" eval (`:186`) guards the additive side.

## Open decision

Whether the `<system_reminder>` wrapper stays in `text` once `source` exists. Keep it:
the transcript records what the model saw, and stripping it would change replay.

## Fix (2026-09-19)

As specified. `ConversationManager` records injected user messages in an
`InjectedSources` map keyed by message identity — a sibling to `_oracleAnswers` rather
than a generalisation of it, so the oracle keeps its `keys`/`verdict` without the two
concerns being entangled. `TranscriptMapper.FromHistory` takes it alongside
`oracleAnswers` and stamps `source`.

Wire values are `"loop"` and `"todo"` — singular, matching the tool name and `tools
-todo`, as the report asked. The turn-dump label still says `"todos"` and is left alone;
it is a human-facing string, not the wire.

The `<system_reminder>` wrapper stays in `text`, per the report's own open decision: the
transcript records what the model actually saw, and stripping it would change replay.

A program that never loops and never leaves a todo open emits no reminder, so its
transcript is unchanged.

## Test

Unit: `InjectedReminders_CarryTheirSource` — a loop message maps to `source: "loop"`, a
todo message to `"todo"`, and a program-authored turn to null. Eval: a Mock program
driven into the doom loop yields at least one `user` event with `source == "loop"`, and a
plain run yields no `user` event carrying the field at all.

Worth noting alongside: the two existing oracle evals assert on reminder *wording*
(`"No one is available"`, `"end the turn and ask"`) precisely because there was no tag to
count. That wording varies at runtime on whether a sheet is attached, which is the
fragility this report was filed about.
