---
kind: plan
title: ATIF out, ACP in — nb on the agent-interchange standards, on one event stream
state: draft
created: 2026-10-27
updated: 2026-10-27
touches:
  files:
    - Program.cs
    - nb.Core/ConversationManager.cs
    - nb.Core/Facade/Nb.cs
    - nb.Core/Facade/NbRuntime.cs
    - nb.Core/ProgramEvaluator.cs
    - nb.Core/Transcript/TranscriptMapper.cs
    - nb.Core/Transcript/TranscriptEvent.cs
    - nb.Core/Transcript/TranscriptSerializer.cs
    - nb.Core/Transcript/ExitReasons.cs
    - nb.Core/Shell/BashTool.cs
    - nb.Core/Harness/HarnessRegistry.cs
    - nb.Core/Harness/NbHarness.cs
    - docs/conversation-program-cli.md
    - evals/run.sh
  features: [transcript, headless, oracle, harness, distribution]
provenance:
  author: claude
  source: conversation 2026-10-27, from proctor's project/learnings/scope.md (ATIF take; ACP napkin, same day)
  peers:
    - plans/agent-neutral-runs.md (this supersedes its two ATIF/ACP open questions, item 2, and part of item 1; executes item 5)
    - ../proctor/project/plans/agent-neutral-runs.md (decisions 4, 9; open question "nb emitting ATIF")
    - ../proctor/project/learnings/scope.md ("ATIF is the standard", "What ACP in nb would cost")
---

# ATIF out, ACP in

Proctor has moved to Harbor, and with it to two standards nb doesn't speak:
**ATIF**, the trajectory format Harbor writes for every agent, and **ACP**, the
JSON-RPC protocol its simulated user drives a target agent over. Today proctor
converts nb's JSONL to ATIF itself (`Run/NbAtif.cs`), and refuses a tool-using nb
arm with an oracle because nb can't be an ACP target. Both repos have been
pointing at the other for the follow-up: proctor's plan calls nb writing ATIF
"nb's call, in its plan", and nb's plan leaves both as open questions. This plan
answers them.

It is three features and one refactor, and the refactor is the point. nb has no
event stream: the transcript is reconstructed *after* the run from
`ConversationManager.History` plus three side tables (`Approvals`,
`OracleAnswers`, `InjectedSources`) in `TranscriptMapper.FromHistory`. That shape
is why per-round usage is summed and thrown away (`ConversationManager.cs:526`),
why `thinking` is never emitted, why compaction silently drops transcript, and
why the ACP napkin priced live updates at +1–2 days. ATIF wants per-step
metrics and timestamps; ACP wants events as they happen. Bolting either onto a
post-hoc rebuild is the hacky codepath we're trying not to write. So both sit on
one append-only **run journal**, and the post-hoc rebuild goes away.

## Findings this plan rests on (2026-10-27)

Checked in source, not inferred, unless marked.

- **Harbor's ACP target list is not hard-coded.** `bridges/acp.py:197` accepts any
  agent implementing `ACPAgentMixin`, whose one abstract method is
  `acp_command() -> list[str]`; `BaseBridge.create` also wants
  `BridgeKind.ACP in agent.capabilities.bridges`. cc, Codex, opencode and
  gemini-cli are simply the built-ins that implement it. Verified in the cached
  Harbor 0.24.0 wheel, proctor's pin. This retires the napkin's largest risk:
  proctor's `NbAgent` gains the mixin and returns `["/opt/nb/nb", "--acp", …]`.
- **In bridge mode Harbor records the target as raw ACP**:
  `agent/bridge-trajectory.json` is `acpx sessions export` (JSON-RPC history,
  `format_version: 1`), and the trial's `trajectory.json` is the *user* agent's.
  Target usage is scraped by `ACPBridge.enrich_context` from the last object
  carrying `inputTokens`/`outputTokens`/… keys; ACP's own `usage_update`
  (`used`/`size`) doesn't match, hence "no usage" in proctor's spike. So the
  target's ATIF must come from nb, written beside the protocol.
- **acpx approves every permission request** (`defaultPermissions:
  "approve-all"`). Irrelevant to nb: its approval is declarative and never asks.
- **ATIF**: pydantic models in Harbor (`models/trajectories/`, `extra="forbid"`
  everywhere), validator `python -m harbor.utils.trajectory_validator`. Main is at
  v1.8 (adds audio parts only); Harbor's own cc/opencode/gemini adapters write
  v1.7, its generic ACP agent v1.6. 1.7 broke 1.6 on subagent refs. `step_id`
  must be contiguous from 1; only `agent` steps may carry `model_name`,
  `reasoning_content`, `tool_calls`, `metrics`; every `source_call_id` must name a
  `tool_call_id` in the same step.
- **ACP v1** (protocol version is the integer `1`): an agent answers
  `initialize`, `authenticate`, `session/new`, `session/prompt`, accepts the
  `session/cancel` notification, and streams `session/update` notifications.
  `stopReason` is `end_turn | max_tokens | max_turn_requests | refusal |
  cancelled`; a cancelled prompt must *answer* `cancelled`, not error. No
  official C# SDK; several community NuGet packages, none we'd want to pin a
  public protocol surface to.
- **Proctor's `NbAtif` is strict on event types** (`Run/NbAtif.cs:104`): an nb
  event type it doesn't know fails the Harbor cell as `adapter_unmapped`. So is
  ingest's tool-name map (`Run/Ingest.cs:66-76`). **The old plan's "additive;
  old readers ignore an unknown event type" (item 2) is false for proctor's
  Harbor path.** A new JSONL event type is a breaking change.
- **nb's run loop already supports several prompts against one runtime**:
  `ProgramEvaluator.EvaluateEventAsync` is public "for the incremental library
  host", `ConversationManager.RunAsync` appends the user turn itself. Budgets
  already have defined multi-`run` semantics: tokens, wall time and
  `oracle_turns` are cumulative; `tool_calls`, the loop detector and the error
  tracker reset per run. An ACP prompt is a `run`, so ACP needs **no new budget
  semantics**, which the napkin had flagged as a risk.
- **The costume precondition is met.** Item 5 of `plans/agent-neutral-runs.md`
  waited for proctor to run a real suite through Harbor on cc, Codex and Qwen
  Code. That happened 2026-10-20 (`20261020-0239-code-change-f3h0`, 24/24 cells,
  proctor 0.3.2). Every proctor suite and config says `harness nb`.

## Both sides of a change

nb and proctor are separate projects with one owner, and the work below edits
both. Proctor builds against nb's master (`../nb/bin/Debug/net10.0/nb`, and the
image's nb must equal the host's) and has no CI of its own, so drift shows up
as a broken proctor run, not a red build. The rule is light: **a change to
anything proctor reads names its proctor side in the same stage, and both land
together.** Neither master stays broken for more than about an hour. There's no
expand–contract ceremony; just never ship one half and forget the other. The
tripwire (stage 0) makes "anything proctor reads" mechanical rather than
remembered.

### What proctor reads (the contract this plan must not break by accident)

From a sweep of proctor at `67a9860`:

| Surface | Where proctor reads it | Strict? |
|---|---|---|
| CLI: `--compile`, `--output jsonl`, `--config`, `-`, `--version` (text before `+`) | `Run/Runner.cs:390-429,537`, `Harbor/nb_agent.py:57` | yes |
| Error text: first `Error…` line on stderr; `Error: @include not found: …` pinned by a test | `Run/Subprocess.cs:8`, `RunnerTests.cs:251` | yes |
| JSONL event types: `system user assistant_text thinking assistant_json tool_call tool_result result` + directive echoes `run provider model harness mcp tools approval loop budget oracle` | `Run/NbAtif.cs:16,104` | **yes**, any other type fails the Harbor cell |
| Event fields: `turn`, `source`, `keys`, `approved`, `approval_reason`, `result{}`, trailer fields | `Grade/Transcript.cs:132-174`, `NbAtif.cs` | tolerant of extras |
| Tool names: `bash read_file list_dir write_file edit_file apply_patch find_files grep fetch_url search_web todo_write todo_read` | `Run/Ingest.cs:66-76` | **yes** |
| Strings: loop nudge (`<system_reminder>` … "repetitive loop"), `Error:` prefix, `[exit code: N]`, the rung as `approval_reason`'s first word | `Grade/Transcript.cs:20-36` | yes |
| Default deny of `echo changed > note.txt` with no approval rules, `Trust: false` | `suites/gate/tasks/denied.json` | yes |
| Publish layout: self-contained `linux-x64` dir with `nb` at its root | `Run/Harbor.cs:40-53` | yes |
| Config: `ChatProviders[].Name/Endpoint/ApiKey`, `${VAR}` | `Run/Runner.cs:355-368` | yes |

## Stages

Each stage is shippable alone and leaves both masters green. Sizes are working
days, from the code as it stands.

### 0. The proctor contract tripwire (½ day, no interop change)

`nb.Tests/ProctorContractTests.cs`: drive the Mock provider through programs that
exercise every event nb can emit, and assert the emitted event-type set and tool
names are subsets of the lists in the table above, and the pinned strings are
unchanged. Each list carries a comment naming the proctor file that holds the
other copy. When it fails, the fix is the proctor change plus the list update,
together; updating the list alone is the forgetting it exists to stop. Cheap,
and it would have caught item 2's wrong assumption.

### 1. Retire the costumes, make `harness` optional (1 day; proctor: one cosmetic edit)

**Done 2026-10-28.** Beyond the list below, two more dead paths went: the whole
`prompts/` directory (per-provider `system*.md` files loaded at startup and never sent),
and the `.nb_turn_dumps/` writer, which also fired on any "suspicious" turn and wrote
into the model's working directory. `NbHarness` is sealed; its costume hooks are gone.
The JSONL bytecode path now refuses a retired harness too, as the parser does.

Executes `plans/agent-neutral-runs.md` item 5 as written: delete the three
costume classes, their prompts, tests and eval rows; the refusal message naming
the replacement is the migration, eval line written first. Un-deprecate
`EditToolStyle: ApplyPatch` and drop its warning.

One change to item 5: settle its open question about the `harness` directive.
`harness` was made required (`2f8e0a3`) so a run could never silently wear the
wrong costume. With one costume left there's nothing to get wrong, so the
requirement goes and **the default is `nb`**. `harness nb` stays valid, and every
existing program and fixture keeps working untouched; any other value gets the
retirement refusal. Removing the directive outright would break every proctor
template (`harness {{harness}}`) and every fixture for no gain. It can be
deprecated later, separately.

Do it **first**, because every later stage gets smaller: the journal refactor
and the ATIF writer have one tool surface and one set of result strings to
preserve instead of four.

Trim in the same pass, since they sit in the code stages 2 and 4 rewrite and
have no callers: `ConversationManager.SendMessageAsync` and
`ClearConversationHistory`, the spinner, the always-false `debugStream`
parameter and its `.nb_turn_dumps/` writer, `ProviderManager.ShowProviders*`,
the dead `ConfigurationService.GetSystemPrompt` load, the `interactive` value
of `--output`, and the Ctrl-C handler's escape write to **stdout**
(`Program.cs:120`, a REPL leftover, and a protocol corrupter under stage 5).
`NoBash` stays: it is used deliberately, for utility runs where tools confuse
the model.

Interop: none for current suites, which all say `harness nb`. Proctor's
validation text (`Suite/Suite.cs:395,520`) lists the costume names; update it
in the same hour.

### 2. The run journal (2–3 days, no interop change)

**Done 2026-10-28.** `nb.Core/Transcript/RunJournal.cs`. The identity gate was checked
directly as well as by the suites: every eval invocation's stdout captured before and
after and diffed, 131 of them, differing only in `nb_version` and a clock tool's output.
One departure from the text below: the compaction change could not be its own commit,
because an append-only journal keeps compacted turns by construction; mimicking the old
loss would have meant writing the deletion just to remove it. It has its own test
(`RunJournalTests`). The other change, `thinking`, is its own commit, covering provider
reasoning content and the inline `<think>` blocks nb already stripped. Not built yet:
journal subscribers. Stage 5 is their first reader, so they arrive with it. Approvals now
land on the call they decided; the old ledger stamped a reused call id's last verdict on
every call carrying it (the Mock reuses ids).

`ConversationManager` appends `TranscriptEvent`s to a `RunJournal` as things
happen: the user turn when it's added, assistant text and tool calls when the
response lands, the approval decision when it's made, the tool result when it
returns, the oracle answer when it's injected. Each entry also carries what the
history can't: a timestamp, the round it belongs to, and that round's measured
or estimated usage and model. Subscribers can watch the journal; the JSONL
emitter, the ATIF writer and the ACP session are its three readers.

`TranscriptMapper.FromHistory` and the three side tables go. `Approvals` keeps
its `DeniedCount`; the rest of its job and `OracleAnswers`/`InjectedSources`
become journal entries written at the point of decision. History goes back to
being what the model sees, and nothing else.

**The gate for this stage is byte-identical JSONL**: every nb test and eval,
and proctor's `dotnet test` against the new build, green with no fixture
touched. Per-round usage and timestamps live on the journal and are **not**
serialized to JSONL. That keeps the wire unchanged, and stage 3 is where they
surface.

Two behaviour changes are deliberate and land as separate commits after the
identity gate passes, each with its own test:

- **Compaction no longer drops transcript.** The journal is append-only;
  compaction rewrites what the model sees, not the record. A compacted run's JSONL
  gains the turns it used to lose. These are known event types, so proctor is
  unaffected.
- **`thinking` is emitted** when a provider returns it. It's a known type in
  both proctor readers, mapping to `reasoning_content`.

### 3. nb writes ATIF (1–1½ days; proctor reads it and deletes `NbAtif`)

`--trajectory <path>` writes an ATIF trajectory of the run to a file, beside
whatever `--output` sends to stdout. The writer is a pure function of the
journal.

**A side file, not `--output atif`.** ATIF is one JSON document, not a stream.
proctor wants both the JSONL and the trajectory from one run on the bare path,
and under ACP stdout belongs to the protocol. One flag serves all three cases.
JSONL stays nb's native format because it is also nb's program format: `--seed`
reads what `--output jsonl` writes.

**The mapping is proctor's, verbatim, plus what only nb can fill.** Start from
`NbAtif.Convert`: one agent step per turn, user and system steps, plain
`extra.source`/`extra.keys`/`extra.approved`/`extra.approval_reason`/
`extra.assistant_json`/`extra.result`, the trailer under `extra.result`,
directives under `extra.directives`, `agent{name: "nb", version, model_name}`.
That way proctor's `Transcript.FromAtif` reads nb's file unchanged. Then add,
from the journal, the spec fields proctor's conversion can't reconstruct:
per-step `metrics` (`prompt_tokens`, `completion_tokens`, `cost_usd` when
priced), `timestamp`, per-step `model_name`, `llm_call_count`, and in
`final_metrics` `total_cost_usd` and `total_steps`. Estimated usage is flagged
under `metrics.extra.estimated`, never passed off as measured, as on the
trailer.

**This replaces the old plan's item 2.** A per-turn `usage` JSONL event was
meant to let proctor's conversion fill step metrics. nb writing the metrics
itself makes that unnecessary, and a new event type would break the Harbor path
(the findings above). JSONL gets no new event type.

**Version: `ATIF-v1.7`**, what proctor reads and what Harbor 0.24's adapters
write. Proctor rejects a newer `schema_version` until read, so a bump is a
change on both sides. The pin is a constant next to the writer, with the reason.

**Validation:**
- In-repo structural tests: contiguous `step_id`; agent-only fields only on
  agent steps; every `source_call_id` matched in its step; no `extra` key
  outside the documented list.
- An opt-in eval that runs Harbor's own validator at proctor's pin when `uv`
  is present (`uv tool run --from harbor==0.24.0 python -m
  harbor.utils.trajectory_validator`), skipped otherwise, as proctor's gate is.
  nb's CI doesn't gain Python.

**Docs:** this is the content `docs/transcript-schema.md` (item 1 of the old
plan, never written) was waiting for. It covers the JSONL events, which are
stable and which are nb-only, and the ATIF mapping. It is also the locked
output spec a downstream consumer is owed.

**Proctor side, in the same stage:** `nb_agent.py` and the bare runner pass
`--trajectory`, ingest reads nb's file, and `NbAtif` is deleted. Before
deleting it, check once on the smoke suite that nb's trajectory and
`NbAtif(transcript.jsonl)` agree, ignoring the added fields. The captured
fixtures become captured trajectories. Proctor's `Parse` survives only as long
as bare cells under `runs/` hold JSONL. This is scope.md's "one reader".

### 4. Cancellation that ends cleanly (½–1 day, no interop change)

ACP's `session/cancel` needs it, and library hosts are owed it already.

- A caller's cancel ends the run with a new exit reason, `cancelled`, instead
  of rethrowing (`ConversationManager.cs:410`).
- `BashTool.ExecuteAsync` takes the token and kills the process tree. Today a
  running command outlives a cancel until its own timeout.

**`cancelled` is a new trailer value.** Proctor side: its ending map learns it
in the same stage. The bare CLI never cancels a run (a Harbor timeout kills the
process, and no trailer is a failed cell as now), so this is bookkeeping until
stage 5.

### 5. `nb --acp <program>` (3 days; plus proctor's bridge side)

nb as an ACP agent on stdio. **Precondition, $0 and about half a day in
proctor:** run the bridge against a stub. That means `NbAgent` gains
`ACPAgentMixin` and its `acp_command` points at a 30-line Python ACP stub, with
`oracle_user.py` talking to it through Harbor 0.24 on the stub model. The
source reading above says this works. The spike confirms it, and also checks
whether usage keys in the prompt response's `_meta` reach
`acp_target_usage` (inferred, not tested).

**One session per process.** The program file configures the session:
provider, model, approval, budgets, `system`, `oracle`, fabricated history.
`run` is refused, because prompts arrive over the protocol. A second
`session/new` is an error. This matches nb's one-run-per-process identity.
It also sidesteps the process-global console that two concurrent sessions
would share (`Nb.cs:49-53`), and acpx and the bridge only ever open one.

**Shape:**

| ACP | nb |
|---|---|
| `initialize` | answer `protocolVersion: 1`, `agentInfo{name: "nb", version}`, `authMethods: []`, `loadSession: false`, `promptCapabilities.image: true` |
| `authenticate` | no-op success; keys come from config, as always |
| `session/new` | `NbRuntime.BuildAsync` + MCP connect + `ProgramEvaluator` over the directive-only program. The client's `mcpServers` are ignored for now, logged on stderr |
| `session/prompt` | `EvaluateEventAsync(new RunEvent{…})`. The journal's subscriber sends each entry as a `session/update`. Answer with `stopReason` |
| `session/cancel` | cancel the prompt's token (stage 4), answer `cancelled` |
| journal → `session/update` | user → `user_message_chunk`; assistant text → `agent_message_chunk` (whole message: nb renders by message, not token); thinking → `agent_thought_chunk`; tool call → `tool_call` (`pending`, then `in_progress`); tool result → `tool_call_update` (`completed`, or `failed`); round usage → `usage_update` |
| exit reason → `stopReason` | `ok`, `oracle_miss` → `end_turn`; `max_tool_calls`, `tool_error_limit`, `oracle_budget` → `max_turn_requests`; `token_budget` → `max_tokens`; `cancelled` → `cancelled`; `time_budget`, `approval_denied` → `end_turn` with the reason in `_meta.nb.exit_reason`; `provider_error`, `rate_limited` → a JSON-RPC error carrying the reason |

The journal is what makes live updates cheap here. The napkin's +1–2 days for
live streaming was the cost of threading a sink through the inline
`MarkdownRenderer` calls, and stage 2 already did that.

**The record beside the protocol.** `--transcript <path>` appends JSONL per
journal entry, so it survives a kill. `--trajectory <path>` rewrites the ATIF
file at the end of every prompt. Proctor ingests these, not
`bridge-trajectory.json`. Each prompt's trailer is a `result` event, as with a
multi-`run` program today.

**stdout discipline.** In `--acp`, nb takes the raw stdout stream for the
protocol writer, then points `Console.Out` at stderr, so any stray write
anywhere lands on stderr instead of corrupting the protocol. A test runs a
Mock session that exercises every tool and warning path, and asserts every
stdout line parses as JSON-RPC.

**Hand-rolled on System.Text.Json**, not a community SDK. The agent side is four
methods and one notification. A public protocol surface shouldn't hang on an
unofficial package, and nb's dependency list stays as it is.

**Tests:** the Mock provider over in-memory pipes covers two prompts in one
session (budgets accumulate as with two `run`s), cancel mid-tool, every
`stopReason`, and the stdout test. An eval pipes a canned request sequence to
`nb --acp` and asserts the responses. nb handles messages in order, so no
client process is needed.

**Proctor opt-in:**
- `NbAgent` gains the mixin, `bridges={ACP}`, and an `acp_command` with
  `--transcript` and `--trajectory` under `/logs/agent/`.
- Validation stops refusing tool-using nb arms with an oracle.
- Ingest reads nb's trajectory in bridge mode.
- The gate that the outside and in-process oracle loops agree on Mock (proctor
  decision 9) becomes runnable. It needs a resolver for the outside loop:
  `nb --judge` (old plan item 4) or proctor's own judge. Proctor's
  `plans/oracle-in-proctor.md` decides which, and nothing in this stage depends
  on the choice.

## Interop summary

| Stage | nb change | Proctor side |
|---|---|---|
| 0 | contract test | — (the test names proctor's files) |
| 1 | costumes refused; `harness` defaults to `nb` | validation text drops the costume names |
| 2 | journal; JSONL byte-identical | run its tests against the build; no edits |
| 2b | compaction keeps transcript; `thinking` emitted | none (known types) |
| 3 | `--trajectory` | pass it; ingest reads it; delete `NbAtif`; fixtures → trajectories |
| 4 | `cancelled` exit reason (library/ACP only) | ending map learns it |
| 5 | `--acp`, `--transcript` | spike first; then mixin, validation, ingest, gate |

**What nothing in this plan does:** add a JSONL event type, rename a tool,
change an error or nudge string, or change the publish layout.

## What this supersedes in `plans/agent-neutral-runs.md`

- Open questions "nb writing ATIF directly" and "nb as an ACP agent": answered here.
- Item 2 (per-turn `usage` event): replaced by ATIF step metrics (stage 3). Its
  premise that unknown events are ignored is wrong for proctor's Harbor path.
- Item 1's trailer `agent`/`agent_version`: ATIF's `agent` block carries both.
  `kind` on `tool_call` stays unbuilt: proctor holds the nb name→kind map at
  ingest, and the tripwire guards the names. `cost_budget` is unaffected.
- Item 5 and its `harness` open question: executed in stage 1.
- Items 3 (`--serve-fake-tools`), 4 (`--judge`) and 6: unchanged, still owed
  there.

## Not in this plan

- **Trust's cwd rule and `bwrap`.** Retiring them is decided
  (`plans/approval-is-not-a-boundary.md`), but they don't touch any code
  here. Proctor runs `Trust: false`, so nothing in this plan depends on them.
- **Moving stderr chrome onto the journal.** `MarkdownRenderer` and roughly 90
  lines of inline Spectre would become one more journal subscriber, removing
  the process-global console. It's a real legibility win, and a follow-on once
  stage 2 has settled, not a gate for anything here.
- **Porcelain output.** Evals use it, so it stays.
- **`session/load`, client-supplied `mcpServers`, permission requests.** The
  bridge needs none of them.
- **ATIF 1.8.** Moves when proctor's Harbor pin does, on both sides.

## Open questions

- **Where this file lives.** The uncommitted `plans/repo-structure-cleanup.md`
  moves `plans/` under `project/` by sed. If that lands first, this plan moves
  with it. That plan's draft constitution says "one contract, three surfaces".
  `--acp` is a mode of the CLI surface serving the same program contract, not
  a fourth surface. The constitution should say so in one line.
- **ACP message granularity.** Whole-message `agent_message_chunk`s are enough
  for the bridge, which blocks per turn. Token streaming would only matter for
  an interactive client like Zed, and nobody has asked for that.
