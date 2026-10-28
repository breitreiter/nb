---
kind: plan
title: Agent-neutral runs — what nb owes when proctor runs real harnesses beside it
state: draft
created: 2026-09-29
updated: 2026-10-14
touches:
  files:
    - Program.cs
    - nb.Core/Transcript/TranscriptEvent.cs
    - nb.Core/Transcript/TranscriptSerializer.cs
    - nb.Core/Transcript/ExitReasons.cs
    - nb.Core/OracleResolver.cs
    - nb.Core/MCP/FakeToolManager.cs
    - nb.Core/ConversationManager.cs
    - docs/containers.md
    - docs/testing.md
    - nb.Core/Harness/HarnessRegistry.cs
    - nb.Core/Harness/ClaudeCodeHarness.cs
    - nb.Core/Harness/CodexHarness.cs
    - nb.Core/Harness/QwenCodeHarness.cs
    - nb.Core/Facade/NbRuntime.cs
    - appsettings.example.json
    - THIRD-PARTY-NOTICES.md
  features: [headless, transcript, oracle, fake-tools, distribution, harness]
provenance:
  author: claude
  source: conversation 2026-09-29; revised 2026-10-14 after proctor adopted Harbor; peer plan ../proctor/project/plans/agent-neutral-runs.md
---

# Agent-neutral runs — nb's half

Proctor is gaining real Claude Code, Codex and Qwen Code arms beside nb's
(`../proctor/project/plans/agent-neutral-runs.md`, which holds the design and
most of the work). This file is what nb owes that design. It is short on
purpose: the design moves machinery *out* of nb, not into it.

## Revisions

- **2026-10-27.** `plans/atif-and-acp.md` answers the two ATIF/ACP open
  questions, replaces item 2 with ATIF step metrics (its "old readers ignore
  an unknown event type" is false for proctor's strict `NbAtif`), and
  schedules item 5 now that proctor's 2026-10-20 run met its precondition.
  Items 3, 4 and 6 are unchanged.
- **2026-10-14.** Proctor adopted Harbor, the Terminal-Bench 2.0 harness, as
  its container runner and vendor-agent layer
  (`../proctor/project/learnings/harbor.md`). Four changes here follow:
  - **The lingua franca is ATIF, not nb's schema.** Harbor already writes
    one trajectory format for ~40 agents. Proctor computes the counted core
    over it at ingest. nb's JSONL stays nb's wire format, and proctor converts
    it (item 1).
  - **All three costumes retire, `qwen-code` included.** This plan's own
    rule retires a costume once proctor has the real harness, and proctor
    now has `agent: qwen-code` (item 5).
  - **The container is Harbor's.** nb is one installed agent among Harbor's
    others. Tool-less nb arms keep running bare on the host (item 6).
  - **The oracle loop under proctor is a simulated user** calling
    `nb --judge` on the host. `--judge` is still owed (item 4).

## The design in one paragraph

A proctor arm names an **agent** (`nb`, `claude-code`, `codex`,
`qwen-code`), and the agent fixes the vendor: cc runs Anthropic models,
Codex OpenAI, Qwen Code Qwen; only nb takes any provider. An arm with tools
runs through **Harbor** in a container. A tool-less nb arm runs bare on the
host, as today. Harbor writes every agent's run as an **ATIF** trajectory.
Proctor converts nb's JSONL to ATIF at ingest, and computes the counted
fields (turns, tool calls, denials, exit reason, tool kind) by its own
definitions, never copying an agent's own count. Run limits live in
`suite.json`, enforced by Harbor's timeout, the agent's own flags, and a
check at ingest. The **oracle** is an answer sheet on the host, consulted
through `nb --judge` by a simulated-user agent proctor gives Harbor.
**Fake tools** are served over MCP so every agent can see them.

## What this means for nb's identity

nb is two things to proctor after this: **one agent** among four, and **the
program language** suites are written in. It is no longer the transcript
schema every agent is normalised to; that role went to ATIF, which already
had the agents. nb does not learn any other agent's CLI flags. Vendor CLIs
change on the vendor's schedule, Harbor and proctor pin their versions, and
nb should not release in lockstep with cc.

What nb stops being, for proctor suites: the enforcer of `budget`, the
driver of the oracle loop, and the host of fake tools. It keeps all three for
nb's own standalone use case, which stays real:

**"We have a chatbot in our product; does it handle common user requests
sensibly?"** No coding harness is involved. There's a product system prompt,
a vendor model called directly, the product's own tools (faked), a
conversation that is partly scripted (fabricated history), and a user who
answers follow-up questions (the oracle). It is an increasingly unusual way to
build a product, but a real one, and nb is the right instrument for it. The
agent-neutral work must not erode it: fabricated history, in-process fake
tools, the in-process oracle loop, `approval`, `loop` and `budget` all stay
first-class. That applies to any vendor's model on nb's own surface, which
is also why GPT keeps its preferred edit tool (item 5, *ApplyPatch*).

Proctor's tool-less arms are the same shape: parsing, extraction and
classification tests where a container and an agent install cost more than
the model call. They run nb bare, and they are why nb stays fast to start and
needs nothing but its binary and a config.

What nb stops being, for everyone: **a stand-in for Claude Code, Codex or
Qwen Code.** Once proctor runs the real harnesses, a costume of the same name
is a second route to the same question, and the worse one. A consumer agent
that reads "run it under `harness claude-code`" in nb's docs and "use
`agent: claude-code`" in proctor's will pick one at random. There should be
exactly one way to ask "does this work in Claude Code?", and it is Claude
Code (item 5).

## Work

### 1. A locked transcript spec (`docs/transcript-schema.md`)

nb's JSONL stops being a private wire format, not because every agent
writes it (they write ATIF now) but because two consumers read it: proctor's
nb → ATIF conversion, and a downstream consumer waiting on a locked spec of
the program structure, which this is the output half of.

Comparability is proctor's job, at ingest. It was this item's job in the
first draft, and the observations that drove it still stand (2026-09-29):
- nb counted `turns: 2` for one prompt plus one tool round-trip, where cc's
  `num_turns` counted 1 for a similar exchange.
- cc's stream interleaves subagent calls (`parent_tool_use_id`), so whether
  they count toward `tool_calls` is a decision, not a fact.
- A check naming `edit_file` fails every cc arm, because cc calls it `Edit`.

Proctor now owns those definitions (`turns`, `tool_calls`, `denied`,
`exit_reason`, the normalised `kind`), computed per agent from ATIF steps.
What nb owes is a transcript that makes its own conversion exact:

- **The spec.** Events `user{text, source?}`, `assistant_text`, `thinking`,
  `tool_call{id, name, kind, arguments, approved, approval_reason?}`,
  `tool_result{id, output, exit_code?}`, `usage` (item 2), and the `result`
  trailer. Which fields are stable, and which are nb-only and marked so
  (`assistant_json`, directive echoes, `source: loop|todo`).
- **`kind` on every `tool_call`.** Normalised: `shell | read | write | edit
  | search | list | web | mcp | agent | todo | other`. nb knows its own
  tools best, so `NbHarness` declares each tool's kind beside its name
  rather than proctor keeping a name map for nb. The values are proctor's
  vocabulary; the schema doc points there.
- **Trailer adds `agent: "nb"` and `agent_version`.** `nb_version` stays for
  compatibility.
- **The exit-reason enum gains `cost_budget`.** Proctor writes it at ingest
  for any agent. Keeping it in `ExitReasons.cs` keeps one list for nb's own
  endings and proctor's, even though nb itself does not enforce cost yet.
  `adapter_unmapped` is proctor's alone and does not come here.

The ATIF mapping itself (nb event → ATIF step, `source: loop` →
`extra.nb.source`) lives in proctor. Whether nb should also write ATIF
directly is an open question.

### 2. Per-turn usage

Today usage appears only in the `result` trailer. Emit a `usage` event after
each model round-trip: `{input, output, estimated, cost?}`. ATIF carries
metrics per agent step, and cc and Codex fill them; this lets nb's conversion
fill them too, and gives any later live supervisor something to read on an
nb arm. Additive; old readers ignore an unknown event type.

### 3. Fake tools over MCP

Every agent Harbor runs for proctor speaks MCP, and Harbor already registers
a task's `[[environment.mcp_servers]]` with each agent. `fake-tools.yaml` is
already a self-contained definition (`FakeToolManager`: name, description,
parameters, response template with macros). Serve it:

```bash
nb --serve-fake-tools fixture/fake-tools.yaml     # stdio MCP server, nothing else
```

Proctor puts the nb binary in the task image and names that command as a
stdio MCP server, so fake tools reach every agent the same way, nb's
included, and one code path is exercised.

**Retired: overriding a built-in tool** (`GetOverriddenTools`). Over MCP a
fake tool is namespaced (`mcp__fixture__lookup_order` in cc) and cannot pose
as `Bash` or `Read`. Faking the built-in surface is what the container fixture
is for: a fake CLI on `PATH` works with bash in every harness. No proctor suite
uses fake tools today (checked 2026-09-29), so nothing that runs breaks. The
in-process fake tools stay, permanently: they are how the product-chatbot
use case fakes the product's tools, and they may keep a tool's bare name
there because no harness surface is being imitated.

### 4. The oracle judge as a command

Proctor drives the oracle loop (its plan, decision 9) as a simulated user:
an agent running in the Harbor process on the host that answers each of the
agent's turns. The judge stays nb's code, reached through the one seam
proctor already uses, the host nb binary:

```bash
nb --judge --sheet sheet.md [--config nb.json] < last-message.txt
# → {"verdict":"hit","keys":["port"],"answer":"8080","raw":"…"} on stdout
```

`OracleResolver.BuildPrompt` and `ParseVerdict` are already pure; this is a
flag that wires them to a provider from config and prints the verdict, so
`evals/oracle-bench` still measures the judge proctor uses. The side call's
provider follows `oracle provider` rules: the named entry, else the config's
active one. `MOCK:oracle=` keeps working for tests. The sheet never enters a
container.

The in-process loop stays, for nb run alone and for proctor's tool-less nb
arms on the bare path, where the sheet is on the host already and the
model has no tool that could read it. A tool-using nb arm with an oracle is
the case neither path covers yet: Harbor's simulated user reaches its target
over ACP, and nb does not speak ACP (*Open questions*).

### 5. Retire the costumes

**The rule a costume survives by:** it is the native surface of a model nb
is the right way to run, and there is no real-harness route for it. After
this plan:

| Costume | Real-harness route | Fate |
|---|---|---|
| `claude-code` | proctor `agent: claude-code` | **retired** |
| `codex` | proctor `agent: codex` | **retired** |
| `qwen-code` | proctor `agent: qwen-code` (Harbor's `qwen-coder`, the real CLI, against imp or the gateway) | **retired** |
| `nb` | — (nb's own surface) | stays, the only value of `harness` |

The first draft kept `qwen-code`: qwen on nb was the free baseline and the
pairing was matched, and the table said the same rule would retire it if
proctor gained `agent: qwen-code`. Proctor has, through Harbor. A Qwen model
on nb's own surface is still `harness nb`.

Why `claude-code` goes without argument: its preamble is a paraphrase
fitted to a closed-source prompt, so it can't be checked against source, and
cc changes on Anthropic's schedule. The `plans/harness-emulation.md` answer
to that was "diff it against a headless control". With real cc as an arm,
the control *is* the product and the costume adds nothing but a second
route. Retiring it also removes the paraphrase and its exposure under that
plan's *legal line*.

Why `codex` and `qwen-code` go too, although both are FOSS and verifiable:
being verifiable is a reason a costume *could* be kept accurate, not a
reason to keep a second route. The harm is the one above: two ways to run
"Codex", one of which isn't Codex.

**What the removal touches:**

- `nb.Core/Harness/ClaudeCodeHarness.cs`, `CodexHarness.cs`,
  `QwenCodeHarness.cs`, `nb.Core/prompts/harness/claude-code.md`,
  `codex.md`, `qwen-code.md`; their entries in `HarnessRegistry`.
- Tests: `ClaudeCodeHarnessTests`, `CodexHarnessTests`,
  `QwenCodeHarnessTests`, and the costume rows in `ToolSurfaceGoldenTests`,
  `DenialGoldenTests`, `HarnessPreambleTests`,
  `HarnessResultFormattingTests`, `HarnessEnvironmentContextTests`,
  `HarnessDirectiveTests`, `HarnessRequiredTests`, `BashTimeoutTests`. The
  Mock provider's `qwen-code` handling (`Providers/Mock/MockProvider.cs`).
- Evals: `evals/fixtures/prog-costume-resolve.nb` has no costume left to
  resolve to and becomes a refusal case. `evals/fixtures/entry-harness-appsettings.json`
  and the `qwen-code` lines in `evals/run.sh` move to `nb`.
- Config: `appsettings.example.json` pairs every entry with `"Harness": "nb"`;
  the pairing comment says so.
- Docs: `README.md` (the *Harness comparison* bullet goes; the costume list
  goes), `docs/providers.md`, `docs/conversation-program-cli.md`,
  `CLAUDE.md` (the `Harness` paragraph and the costume list),
  `HarnessRegistry.RequiredMessage`, and the Qwen Code attribution in
  `THIRD-PARTY-NOTICES.md` once its prompt is gone.
- `plans/harness-emulation.md` gets a *Revisions* entry: the costumes
  retired 2026-xx-xx, and real harnesses in proctor replace them. It is not
  rewritten.

**The refusal is the migration.** A program or config that says
`harness claude-code`, `harness codex` or `harness qwen-code` fails before
any model call, with a message naming the replacement:

> `harness claude-code` was retired on 2026-xx-xx. To test in Claude Code, run
> Claude Code: a proctor arm with `"agent": "claude-code"`. To run an
> Anthropic model on nb's own surface, write `harness nb`.

Write that eval line first and watch it fail; it is a model- and
human-read string, the kind `CLAUDE.md` says to test first. Unknown-name
refusal is not good enough here: a config written last month should say
what happened, not that it never existed.

**The one reversal: `EditToolStyle: ApplyPatch` is un-deprecated** (decided
2026-09-29). It was
deprecated (2026-08-15) in favour of `harness codex`, on the grounds that it
was "a fragment" of the Codex surface. With the costume gone, a GPT model
run on nb standalone still does better with `apply_patch` than with
`edit_file`, and a fragment is now exactly the right claim: nb offers GPT
its preferred edit tool without pretending to be Codex. Remove the
startup warning, `EditToolStyleDeprecationTests`, and the deprecation note in
`CLAUDE.md` and `docs/providers.md`. The plumbing (`NbHarness.ApplyPatchStyle`)
is already in place.

### 6. The container layer

Harbor composes the container now, not proctor and not nb. nb inside it is a
Harbor installed agent that lives in proctor (`harbor/nb_agent.py`). It
installs the pinned nb binary into the task image and pipes the
host-compiled program on stdin. What nb owes:

- **A binary Harbor can install by version.** The `Containerfile`'s
  `/opt/nb` layer, or a release artifact at a stable URL. Whichever the agent
  can fetch with a pinned version and verify with `nb --version`.
- `docs/containers.md` shrinks to that, the one env var
  (`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) and ownership. The invocation
  section moves to proctor.

Tool-less nb arms never see a container; nothing changes for them.

## Not in this plan (and why)

- **`--compile --target <agent>`.** An earlier draft had nb emit each
  agent's CLI invocation. Rejected: it couples nb's releases to cc's and
  Codex's. `--compile` stays agent-neutral; proctor reads the compiled events
  and refuses per agent.
- **Adapters for other agents' streams.** The first draft had proctor
  translate each vendor's stdout into nb's schema. Harbor writes ATIF for
  them, so neither repo writes those adapters.
- **A limits block.** Limits are run policy and live in `suite.json`.
  Proctor refuses `budget` directives in a suite's program with a pointer to
  its porting guide. nb's own `budget` is unchanged for nb run alone.
- **`loop` and `approval` for other agents.** Neither is a limit: `loop`
  injects a nudge the model reads; `approval` is a refusal the model reacts
  to. Both are agent behaviour. nb keeps them; cc and Codex have their own
  approval mechanisms, which proctor configures; nothing replaces `loop` on
  other agents, and proctor reports `not_nudged` as n/a there.
- **A cc mod.** Considered as the transcript source; rejected because
  `claude -p --output-format stream-json` already streams tool calls, results,
  per-message usage, cost and `permission_denials` (and Harbor now reads it),
  and a mod's `$.fs.write` cannot append, so it would be rewriting a growing
  file inside the container where the model can reach it. Fake tools go over
  MCP (item 3), and there's no costume left to calibrate against a captured
  prompt (item 5).

## Staging

1. Schema doc, `kind`, `agent`/`agent_version`, `cost_budget` (item 1).
   Unblocks proctor's nb → ATIF conversion.
2. `--judge` (item 4). Unblocks proctor's oracle user.
3. Per-turn `usage` event (item 2).
4. `--serve-fake-tools` (item 3).
5. Costume retirement (item 5), **after proctor has run a real suite through
   Harbor on `claude-code`, `codex` and `qwen-code`**. Retiring a costume
   before its replacement works leaves no route at all. It can land in one
   commit; the refusal message is the only compatibility shim.
6. The container layer (item 6), when proctor's `harbor/nb_agent.py` lands.

Each is independently shippable and each gets eval lines in `evals/run.sh`
(the evals assert on the model-visible strings: the verdict JSON, the new
event, the refusal of an override).

## Open questions

- `--judge` input: the last assistant message as text, or the transcript so
  far on stdin with nb finding the last message itself. The transcript is
  more robust (nb owns the rule for "last message") and costs nothing.
- **nb writing ATIF directly** (`--output atif`). Proctor converts at ingest,
  which is enough to start. nb writing it would make the bare path and the
  Harbor path identical by construction, and would make nb's runs readable
  in Harbor's viewer. It would cost a second output format to keep in step
  with ATIF's versions, which broke between 1.6 and 1.7.
- **nb as an ACP agent.** Harbor's simulated user reaches its target over ACP
  (cc, Codex, Gemini and opencode today). Without it, a tool-using nb arm
  can't take proctor's outside oracle. Proctor's gate that the outside and
  in-process oracle loops agree, which needs the Mock provider and therefore
  nb, can't run either. Worth sizing once the spike shows the oracle user
  works for cc.
- Whether the `harness` directive survives with one value, or retires with
  the costumes. One value is a directive that says nothing. But dropping it
  breaks every program that writes `harness nb`, and
  `plans/harness-emulation.md` made it required for a reason.
- ~~Whether the in-process fake-tool path and oracle loop are retired once
  proctor stops using them.~~ Settled 2026-09-29: kept. They serve the
  product-chatbot use case (*What this means for nb's identity*), not just
  a proctor transition. Proctor's tool-less arms now use the in-process
  oracle as well (item 4).
