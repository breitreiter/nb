---
kind: bug
title: 'Feature: resume a lost run from its own log'
created: 2026-09-08
updated: 2026-09-08
status: current
state: open
severity: medium
cluster: transcript-replay
---

# Feature: resume a lost run from its own log

Status: Open (2026-09-08) — filed after a run of eval losses, where a program
that had already spent most of its budget ended on an error and the only way
forward was to start it again from the top.

## What is wanted

A first-class *"trim the error off this run log and carry on from where it
stopped"*. Concretely, one invocation that takes a `--output jsonl` log, repairs
whatever the failure left dangling, re-establishes the envelope the original run
was under, and continues:

```bash
nb --resume run-that-died.jsonl continue.nb
```

The motivating loss is not a rare one: a long program dies on `provider_error`
or `token_budget` after twenty rounds of real tool work, and every one of those
rounds is on stdout, complete, and unusable.

## The premise holds: the log already is the program format

This is not a new format or a new loader. `--output jsonl` and `--seed` share
one schema (`nb.Core/Transcript/TranscriptSerializer.cs`), and the seed loader
already ignores exactly what a resume would want dropped — `thinking`,
`assistant_json` and the `result` trailer are enrichment, skipped on load
(`TranscriptLoader.cs:124`, `IsCore`). **The trailer needs no trimming at all.**
It is inert on input.

Verified end to end against `bin/Debug/net10.0` on 2026-09-08. A synthetic
crashed log — a `provider_error` trailer and one unanswered `tool_call` —
resumes as soon as the dangling line is removed, and nothing else needs
touching:

```bash
# crashed.jsonl: user turn, assistant text, tool_call with no tool_result, result trailer
$ grep -v tool_call crashed.jsonl > trimmed.jsonl
$ echo 'run <prompt>' | ./nb - --seed trimmed.jsonl --output jsonl
# → history reconstructed, run continues, trailer inert
```

So the feature is a repair pass plus an envelope, not an engine change.

## Which losses this reaches, and which it does not

The jsonl is emitted **once, after the whole program returns** — `EmitJsonl`
(`Program.cs:308`) is called from `RunProgramAsync` after `Nb.RunAsync`. That
splits lost runs into two classes, and only one of them has a log to resume:

**Reaches the trailer** — `provider_error`, `rate_limited`, `max_tool_calls`,
`tool_error_limit`, `token_budget`, `time_budget`, `approval_denied`. These are
*outcomes*, not exceptions (`ConversationManager.cs:712-724` classifies the
throw and returns a reason), so the full transcript reaches stdout with a
non-zero exit. **This feature covers these completely.**

**Never emits a byte** — Ctrl-C (`Program.cs:123` exits straight from the
handler), a dead MCP server, `ProviderUnavailableException`,
`NbStartupException`, a crash, an OOM. There is no log, so there is nothing to
trim.

⚠️ **Decide which class is actually costing runs before building this.** If the
losses are mostly the second class, the feature that stops them is incremental
emission — a `--log <file>` written as events happen — and that is a larger,
separate change that this one does not imply. This report assumes the first
class, which is where the observed losses have been.

## Gap 1 — the dangling tool call, the only hard blocker

```
Error: tool_call id "call_1" (turn 1) has no matching tool_result — a seed must end on a completed round
```

`TranscriptLoader.cs:107`. The v1 seed contract requires completed rounds, and a
failure lands squarely between the halves of one: the assistant message carrying
its `FunctionCallContent` joins history at `ConversationManager.cs:441`, and the
tool message with the results only at `:636`. Die in between — which is where a
provider error, a rate limit and a budget abort all land — and the log ends on
an unanswered call.

This is the trim, and it is the whole of the trim. Note the design choice inside
it, because the two options are not equivalent:

- **Delete the unanswered call.** Cheapest. But it rewrites what the model
  believes it did, and the model will not re-issue a call it has no record of
  making.
- **Synthesize a result** — `[run interrupted; result unrecorded]`. Keeps the
  round complete and honest, and is almost certainly right when the tool had
  already *run* and its side effects are on disk. A resumed agent that is told
  its `bash` call went unrecorded can re-check; one whose call vanished cannot
  know to.

A partially-answered round (calls A and B, only A answered) works either way —
dropping B leaves A paired — but the same honesty argument applies.

## Gap 2 — the log records no envelope

This is the one that makes a naive resume dangerous rather than merely awkward,
and it is arguably the more valuable half of the report.

The emitted events come from `TranscriptMapper.FromHistory(...)` — conversation
*messages* only. **No `provider`, `model`, `harness`, `tools`, `approval` or
`budget` event ever reaches the output.** The trailer carries `provider` and
`harness` as prose fields; per
`bugs/Effective_Model_Is_Not_On_The_Trailer.md` it does not carry the model at
all.

So a resumed log runs the recovered message history under **whatever config
default happens to be live**, silently. Observed while testing the above: a log
whose trailer read `"provider":"Mock"` resumed against the configured
`ActiveProvider` and produced a transcript indistinguishable from a legitimate
one. For a corpus of eval runs that is worse than losing the run — a
mis-attributed transcript is a wrong answer that nothing flags.

The fix is to emit the config directives into the stream as the evaluator
applies them, which is what "the output contract doubles as the input contract"
(`plans/composable-cli-reorientation.md:300`) already promises and does not yet
deliver. It is worth doing on its own merits, resume or no resume: it is the
same argument that put `provider` and `harness` on the trailer, carried to its
conclusion.

**Snag to design around:** replaying a `harness` directive re-runs
`ApplyHarness`, which inserts `LeadingContext()` as system messages
(`ProgramEvaluator.cs:131-135`). Those system messages are *already* in the log
from the original run, so a naive replay sends the costume's preamble twice.
Either the replay suppresses the insertion, or preamble-origin system events are
marked so a resume can tell them from the program's own `system` directives.

## Gap 3 — turn numbers collide across the seed boundary

Filed separately as `bugs/Seed_And_Program_Turn_Numbers_Collide.md`: any
standalone `user` / `system` / `assistant` line in a program body after a
`--seed` fails validation, because each program numbers its turns from zero. A
resume has to renumber across the join regardless, so it fixes this on the way
past — but the defect exists in `--seed` today, without this feature.

## Proposed shape

Split it. The halves are useful separately and the first is testable with no
model contact at all:

- **`nb --repair <log> [-o out.jsonl]`** — a pure transform. Drop the trailer,
  resolve dangling calls, renumber turns. No provider, no tokens, inspectable
  before anything is spent, and coverable in `evals/run.sh --skip-llm`.
- **`nb --resume <log> [program]`** — repair, re-establish the envelope from the
  directives recorded in the log, then append the program body.

`--resume` depends on gap 2. Shipping it first would produce a tool that quietly
answers with the wrong provider, which is the failure mode nb has been
deliberately closing off elsewhere.

## Notes

- `--resolve` already computes the effective envelope at each run point, from a
  program. Recording directives in the log would let it do the same for a
  recorded run, which is a second reason to want gap 2 closed.
- Images still do not round-trip (schema v1) — a resumed run sees the text note,
  not the image. Pre-existing, not made worse here, but worth stating in the
  docs for `--resume`.
- Seeds deliberately do not populate `FileReadTracker`
  (`plans/composable-cli-reorientation.md`), so a resumed agent re-reads before
  editing. That is correct for a fabricated seed and arguably wrong for a
  resume, where the read genuinely happened. Left as an open question rather
  than assumed either way.
