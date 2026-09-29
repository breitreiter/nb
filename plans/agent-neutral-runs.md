---
kind: plan
title: Agent-neutral runs — what nb owes when proctor runs real harnesses beside it
state: draft
created: 2026-09-29
updated: 2026-09-29
touches:
  files:
    - Program.cs
    - nb.Core/Transcript/TranscriptEvent.cs
    - nb.Core/Transcript/TranscriptSerializer.cs
    - nb.Core/OracleResolver.cs
    - nb.Core/MCP/FakeToolManager.cs
    - nb.Core/ConversationManager.cs
    - docs/containers.md
    - docs/testing.md
    - nb.Core/Harness/HarnessRegistry.cs
    - nb.Core/Harness/ClaudeCodeHarness.cs
    - nb.Core/Harness/CodexHarness.cs
    - nb.Core/Facade/NbRuntime.cs
    - appsettings.example.json
  features: [headless, transcript, oracle, fake-tools, distribution, harness]
provenance:
  author: claude
  source: conversation 2026-09-29; peer plan ../proctor/project/plans/agent-neutral-runs.md
---

# Agent-neutral runs — nb's half

Proctor is gaining real Claude Code and real Codex arms beside nb's
(`../proctor/project/plans/agent-neutral-runs.md`, which holds the design and
most of the work). This file is what nb owes that design. It is short on
purpose: the design moves machinery *out* of nb, not into it.

## The design in one paragraph

Every proctor cell runs in a podman container. An arm names an **agent**
(`nb`, `claude-code`, `codex`), and the agent fixes the vendor: cc runs
Anthropic models, Codex runs OpenAI models, only nb takes any provider.
Proctor owns each agent's invocation and a stream adapter that turns the
agent's stdout into **nb's transcript schema**, which is the one format the
grader reads. Run limits (tool calls, wall clock, tokens, cost) live in
`suite.json` and are enforced by a proctor supervisor that kills the container.
The **oracle loop** moves up into proctor and continues any agent by resume,
with the answer sheet staying on the host. **Fake tools** are served over MCP
so every agent can see them.

## What this means for nb's identity

nb is three things to proctor after this: **one agent** among three, **the
program language** suites are written in, and **the transcript schema** every
agent's output is normalised to. nb does not learn any other agent's CLI
flags; vendor CLIs change on the vendor's schedule, proctor's image pins
their versions, and nb should not release in lockstep with cc.

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

What nb stops being, for everyone: **a stand-in for Claude Code or Codex.**
Once proctor runs the real harnesses, a costume of the same name is a second
route to the same question, and the worse one. A consumer agent that reads
"run it under `harness claude-code`" in nb's docs and "use `agent:
claude-code`" in proctor's will pick one at random. There should be exactly
one way to ask "does this work in Claude Code?", and it is Claude Code (item 5).

## Work

### 1. Lock the transcript schema (`docs/transcript-schema.md`)

It stops being nb's private wire format and becomes the contract two repos
and three agents write to. Owed anyway (a downstream consumer is waiting on a
locked spec of the program structure; this is the output half of it).

- **The minimum** every agent must produce, which is exactly what proctor's
  grader reads (`../proctor/Grade/Transcript.cs`):
  `user{text, source?}`, `assistant_text`,
  `tool_call{id, name, arguments, approved, approval_reason?}`,
  `tool_result{id, output, exit_code?}`, and the `result` trailer
  `{exit_reason, usage, turns, tool_calls, duration_ms, provider, model,
  harness?, denied, cost?, oracle_turns?}`.
- **Everything else is nb-only** and marked so: `thinking`, `assistant_json`,
  directive echoes, `source: loop|todo`.
- **Two trailer fields added:** `agent` (`nb` | `claude-code` | `codex`) and
  `agent_version`. nb writes `agent: nb` and its version; proctor's adapters
  write the rest. `nb_version` stays for compatibility.
- **The exit-reason enum is shared.** Proctor's supervisor writes
  `max_tool_calls`, `time_budget`, `token_budget` and a new `cost_budget`
  from the same list (`nb.Core/Transcript/ExitReasons.cs`), so a trip reads
  the same on every arm. Add `cost_budget` in nb even though nb itself does
  not enforce it yet.

### 2. Per-turn usage

Today usage appears only in the `result` trailer, so nothing watching the
stream can enforce a token or cost limit on an nb arm while it runs. Emit a
`usage` event after each model round-trip: `{input, output, estimated,
cost?}`. cc's stream-json already carries usage per assistant message and
Codex's per turn; this brings nb level with them. Additive; old readers
ignore an unknown event type.

### 3. Fake tools over MCP

All three agents speak MCP. `fake-tools.yaml` is already a self-contained
definition (`FakeToolManager`: name, description, parameters, response
template with macros). Serve it:

```bash
nb --serve-fake-tools fixture/fake-tools.yaml     # stdio MCP server, nothing else
```

Proctor puts that in every arm's MCP config, nb's included, so fake tools
reach every agent the same way and one code path is exercised.

**Retired: overriding a built-in tool** (`GetOverriddenTools`). Over MCP a
fake tool is namespaced (`mcp__fixture__lookup_order` in cc) and cannot pose
as `Bash` or `Read`. Faking the built-in surface is what the container fixture
is for: a fake CLI on `PATH` works with bash in every harness. No proctor suite
uses fake tools today (checked 2026-09-29), so nothing that runs breaks. The
in-process fake tools stay, permanently: they are how the product-chatbot
use case fakes the product's tools, and they may keep a tool's bare name
there because no harness surface is being imitated.

### 4. The oracle judge as a command

Proctor drives the oracle loop (its plan, *Oracle*). The judge stays nb's
code, reached through the one seam proctor already uses, the host nb binary:

```bash
nb --judge --sheet sheet.md [--config nb.json] < last-message.txt
# → {"verdict":"hit","keys":["port"],"answer":"8080","raw":"…"} on stdout
```

`OracleResolver.BuildPrompt` and `ParseVerdict` are already pure; this is a
flag that wires them to a provider from config and prints the verdict, so
`evals/oracle-bench` still measures the judge proctor uses. The side call's
provider follows `oracle provider` rules: the named entry, else the config's
active one. `MOCK:oracle=` keeps working for tests.

The in-process loop stays for nb run alone. Under proctor, nb arms get the
same outside loop as cc and Codex (proctor seeds nb with the prior transcript
and the answer), so oracle turns compare across arms. Proctor's plan gates
this on a Mock-driven check that the outside loop and the inside loop produce
the same transcript apart from bookkeeping.

### 5. Retire the `claude-code` and `codex` costumes

**The rule a costume survives by:** it is the native surface of a model nb
is the right way to run, and there is no real-harness route for it. After
this plan:

| Costume | Real-harness route | Fate |
|---|---|---|
| `claude-code` | proctor `agent: claude-code` | **retired** |
| `codex` | proctor `agent: codex` | **retired** |
| `qwen-code` | none; qwen on nb is the free baseline and the pairing is matched | stays |
| `nb` | — (nb's own surface) | stays |

If proctor ever gains `agent: qwen-code`, the same rule retires that one too.

Why `claude-code` goes first and without argument: its preamble is a
paraphrase fitted to a closed-source prompt, so it can't be checked against
source, and cc changes on Anthropic's schedule. The `plans/harness-emulation.md`
answer to that was "diff it against a headless control". With real cc as an
arm, the control *is* the product and the costume adds nothing but a second
route. Retiring it also removes the paraphrase and its exposure under that
plan's *legal line*.

Why `codex` goes too, although it is FOSS and verifiable: being verifiable
is a reason it *could* be kept accurate, not a reason to keep a second route.
The harm is the one above: two ways to run "Codex", one of which isn't Codex.

**What each removal touches:**

- `nb.Core/Harness/ClaudeCodeHarness.cs`, `CodexHarness.cs`,
  `nb.Core/prompts/harness/claude-code.md`, `codex.md`; their entries in
  `HarnessRegistry`.
- Tests: `ClaudeCodeHarnessTests`, `CodexHarnessTests`, and the costume rows
  in `ToolSurfaceGoldenTests`, `DenialGoldenTests`,
  `HarnessPreambleTests`, `HarnessResultFormattingTests`,
  `HarnessEnvironmentContextTests`, `HarnessDirectiveTests`,
  `HarnessRequiredTests`, `BashTimeoutTests`.
- Evals: `evals/fixtures/prog-costume-resolve.nb` moves to `qwen-code`.
- Config: `appsettings.example.json` pairs Anthropic and OpenAI entries with
  `"Harness": "nb"`; the pairing comment changes to say so.
- Docs: `README.md` (the *Harness comparison* bullet goes; the costume list
  shrinks), `docs/providers.md`, `docs/conversation-program-cli.md`,
  `CLAUDE.md` (the `Harness` paragraph and the costume list),
  `HarnessRegistry.RequiredMessage`.
- `plans/harness-emulation.md` gets a *Revisions* entry: the two costumes
  retired 2026-09-xx, real harnesses in proctor replace them. Not rewritten.

**The refusal is the migration.** A program or config that says
`harness claude-code` or `harness codex` fails before any model call, with a
message naming the replacement:

> `harness claude-code` was retired on 2026-09-xx. To test in Claude Code, run
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

`Containerfile` stays: it publishes nb into `/opt/nb` for proctor's image to
`COPY --from`. `docs/containers.md` shrinks to the layer, the one env var
(`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) and ownership; the invocation
section moves to proctor, which now composes `podman run` itself.

## Not in this plan (and why)

- **`--compile --target <agent>`.** An earlier draft had nb emit each
  agent's CLI invocation. Rejected: it couples nb's releases to cc's and
  Codex's. `--compile` stays agent-neutral; proctor reads the compiled events
  and refuses per agent.
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
  per-message usage, cost and `permission_denials`, and a mod's `$.fs.write`
  cannot append, so it would be rewriting a growing file inside the
  container where the model can reach it. Its other two possible jobs are
  gone as well: fake tools go over MCP (item 3), and there's no
  `claude-code` costume left to calibrate against a captured prompt (item 5).

## Staging

1. Schema doc + `agent`/`agent_version`/`cost_budget` (item 1). Unblocks
   proctor's adapters.
2. Per-turn `usage` event (item 2). Unblocks proctor's supervisor for nb arms.
3. `--judge` (item 4). Unblocks proctor's oracle loop.
4. `--serve-fake-tools` (item 3).
5. Costume retirement (item 5), **after proctor's `Agents/ClaudeCode` and
   `Agents/Codex` have run a real suite**. Retiring the costume before the
   replacement works leaves no route at all. It can land in one commit; the
   refusal message is the only compatibility shim.
6. `docs/containers.md` shrink (item 6), after proctor's runner lands.

Each is independently shippable and each gets eval lines in `evals/run.sh`
(the evals assert on the model-visible strings: the verdict JSON, the new
event, the refusal of an override).

## Open questions

- `--judge` input: the last assistant message as text, or the transcript so
  far on stdin with nb finding the last message itself. The transcript is
  more robust (nb owns the rule for "last message") and costs nothing.
- ~~Whether the in-process fake-tool path and oracle loop are retired once
  proctor stops using them.~~ Settled 2026-09-29: kept. They serve the
  product-chatbot use case (*What this means for nb's identity*), not just
  a proctor transition.
