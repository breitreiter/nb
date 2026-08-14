---
kind: plan
title: Harness emulation — wearing another agent's tool surface
created: 2026-08-14
updated: 2026-08-14
status: current
state: active
supersedes: [plans/tool-dialects.md]
touches:
  files:
    - nb.Core/ConversationManager.cs
    - nb.Core/Facade/NbRuntime.cs
    - nb.Core/MCP/FakeToolManager.cs
    - nb.Core/Transcript/ProgramParser.cs
    - nb.Core/Transcript/ToolSurface.cs
    - nb.Core/Transcript/TranscriptPorcelainWriter.cs
    - docs/conversation-program-cli.md
  features: [tool-surface, provider-config, transcript, prompt-layers]
provenance:
  author: claude
---

# Harness emulation — wearing another agent's tool surface

## Context

nb is used to simulate two different things, and they want opposite things from the
tool layer.

**Job one — your own harness.** You bring your real system prompt and the tool set
you curate for your agent. nb's canonical tools are the truth. Nothing to emulate.

**Job two — someone else's harness.** Claude Code, Codex, Cursor, qwen-code. Each has
a well-known (in some cases published) system prompt and a specific tool surface. The
goal is not a perfect clone; it is a *plausible placebo* — enough for a model to
behave, for a few turns, as though it were running in that harness. That is enough to
make cross-harness comparison mean something.

For job two the prompt half is tractable today: a program's `system` directives can
carry a paraphrased preamble, and `bugs/Tool_Names_Diverge_From_Model_Native_Surface.md`
measured a 10× swing in tool selection from one added sentence of steer. The missing
half is a **conforming tool surface**, and nb has no lever for it at all.

`plans/tool-dialects.md` proposed a rename table (`edit_file → edit`,
`path → file_path`) selected from provider config. That is the right fix for a narrow
repair case and the wrong shape for this one. This plan supersedes it.

## Why a rename table cannot do the job

Two reasons, both discovered by naming the actual targets rather than one model's
misbehaviour.

**Schemas differ structurally, not just by name.** A map turns `edit_file(path, …)`
into `edit(file_path, …)`. It cannot produce `MultiEdit(file_path, edits[])` — a
list-of-edits shape nb has no canonical equivalent of. Same for Grep's `output_mode`
enum and its specific member values, Bash's `run_in_background`, Read's
notebook/PDF/image branching. These are new shapes over existing implementations, and
shapes are code.

**Result formatting is behaviour, not data.** Line-number prefixes on reads, the exact
edit acknowledgment string, truncation notices, how an error comes back. Models are
trained on the observation side of the loop as hard as the action side, so this is
plausibly a larger fidelity lever than the names — and a table cannot express any of
it. A class owns it for free.

nb happens to be well placed here by accident: `ConversationManager` hand-dispatches
rather than auto-invoking (`ConversationManager.cs:527-800`), so every result string is
already nb's own to shape.

## Decisions

- **Inheritance, not configuration.** A harness is a C# class. `NbHarness` is concrete
  and declares nb's canonical surface — the default is a real object, not a null check.
  Each costume derives from it and overrides only what it changes, so a costume reads
  as a diff against something legible.
- **A closed, in-tree set.** `ClaudeCode`, `Codex`, `Cursor`, `QwenCode`. If you need
  something weird, you write a class. No profile DSL, no config language, no discovery.
- **No interface yet.** There is no extension boundary today, and per the project's
  standing guidance an interface does not get invented for future flexibility. The door
  stays open — see *Future door* below — but it is not walked through now.
- **No generic-OpenAI costume.** nb's canonical surface already *is* the generic one;
  `NbHarness` occupies that slot. A second class meaning the same thing invites the
  question of how they differ, forever.
- **Selection is a program directive**, not provider config. See *Selection* below.
- **A named harness brings its prompt.** Opting into a costume opts into the whole
  costume. See *The preamble arrives with the costume* below.
- **Fidelity is bounded, and the bound is a rule.** Reproduce every *channel* of injected
  context, not every *source* feeding one; declare what is skipped; let the control
  promote anything mis-tiered. See *Fidelity is bounded on purpose*.
- **Shape is replicated; prose is authored here.** See *The legal line* below.
- **Transcripts record wire names and the active harness.** A corpus of runs is
  uninterpretable across more than one costume otherwise, for the same reason it would
  be without the model name.

## The legal line

There is a real distinction available, and it is a boundary rather than a hedge.

- **Interface facts** — tool names, parameter names, types, required-ness, enum values,
  schema shape. Functional interface declaration. Reimplementing it to interoperate is
  defensible territory.
- **Expression** — description prose, system prompt text, error message wording. This
  is where copying gets uncomfortable.

So: **replicate the shape, author the prose.** `ClaudeCodeHarness` declares
`Edit(file_path, old_string, new_string, replace_all)` with an nb-written description
of what that tool does, derived from observed behaviour.

**Where the expression is openly licensed, use it.** The rule above is about material we
have no right to — it is not a blanket preference for paraphrase. Two of the four
costumes have Apache-2.0 prompts that can be vendored with attribution, at full fidelity
and zero risk. See *Sourcing the preambles*. Paraphrase is the fallback for closed
harnesses, not the house style.

This is also better on non-legal grounds. A costume built from nb-authored descriptions
is inspectable and stable — when a model behaves differently under it, the rows that
differ can be pointed at. A pasted upstream prompt is a black box that additionally
rots in silence when upstream changes.

## What a harness owns

Four things, in descending order of how much they matter:

1. **The advertised surface** — which tools exist, their wire names, their JSON schemas.
2. **Result formatting** — the exact text a tool call returns, including truncation and
   error shape.
3. **A prompt preamble** — a paraphrased system-prompt fragment the costume contributes
   ahead of the program's own `system` directives, injected on opt-in with no second
   directive required. `NbHarness` contributes none.
4. **Context furniture** — the non-prompt context the real harness injects: an
   environment block (cwd, platform, date, git state), project instruction files, and
   any wrapper convention around injected content (Claude Code's `<system-reminder>`,
   for instance). Promoted to first-class because the moment the control is a real
   headless harness run, furniture is inside the comparison: a costume that nails the
   tool surface and omits the furniture produces a diff that will be misattributed to
   the prompt.

It does *not* own: approval, `TrustSandbox`, `FileReadTracker`, budgets, doom-loop
detection. Those keep seeing canonical tool identities (see *Vocabulary*).

## Fidelity is bounded on purpose

nb is not trying to *be* Claude Code. It is trying to put a model in a context plausible
enough to measure it over a few turns. Fidelity is instrumental, and unbounded fidelity
has a name — reimplementing the harness — which is both infinite work and precisely
where the legal risk lives. "Good enough" is the specification, not a compromise against
it.

The rule that makes that operational: **fidelity is owed to channels, not to coverage.**
A costume must reproduce every distinct *kind* of context the real harness injects — in
the right position, with the right wrapper. It need not reproduce every *source* that
could feed a channel it already has.

Three tiers follow:

1. **Must be real.** Channels whose absence changes model behaviour inside the measured
   window. A fake Claude Code that ignores the project's `CLAUDE.md` is a gnarly miss —
   an entire class of instruction never arrives.
2. **Must exist, may be fake.** Channels the model can see or reach but which will not
   fire in a few turns — `Skill`, `Task`, `WebSearch`, `NotebookEdit`. Presence is what
   matters; backing does not. This is what the fake-tool substrate is for.
3. **May be omitted.** Additional sources feeding a channel that is already present and
   correctly shaped, and anything the model cannot observe. Not walking up to
   `~/.claude/CLAUDE.md` is fine — the project file already fills that channel. Having no
   skills at all is fine.

**The control is the governor.** These tiers do not have to be guessed correctly in
advance. A miscategorised omission shows up as an open behavioural diff against the real
harness, which promotes it. Default to the cheap tier and let the measurement force
upgrades — that is what makes "eh, fine, good enough" a discipline rather than a shrug.

**Declare the omissions.** Each costume states what it knowingly skips, surfaced in the
transcript alongside the harness name. A surprising diff then arrives with a suspect list
attached, instead of sending someone hunting through a costume's source for what it
quietly does not do. Silent approximation is what turns a bounded placebo into an
unfalsifiable one.

## Design

### Shape

`nb.Core/Harness/NbHarness.cs` — a concrete class holding the native tool instances
that `NbRuntime` builds today, with virtual members for the three things above.
Costumes live beside it (`ClaudeCodeHarness.cs`, `CodexHarness.cs`, …) and override
per tool.

The base declares the canonical surface exactly as `ConversationManager.cs:330-403`
does now, so `NbHarness` is a behaviour-preserving extraction before any costume
exists. That extraction is the first commit and should be independently verifiable.

### Constructor cleanup, paid for by the refactor

`ConversationManager` currently takes bash / readFile / writeFile / editFile /
findFiles / grep / listDir / fetchUrl / searchWeb / applyPatch as ten positional
parameters (`NbRuntime.cs:119-124`). A harness object replaces that entire run with
one, and `GetAvailableTools()` (`ConversationManager.cs:~2143`) — today a
hand-maintained mirror of the assembly block, with comments at `:329` and `:2141`
begging the two to be kept in sync — collapses into asking the harness what it
declares. The duplication goes away rather than doubling.

### The two choke points

Unchanged from the dialects analysis; they are the right seams regardless of mechanism.

1. **Outbound (registration).** The harness supplies the `AIFunction` list, so wire
   names and schemas are whatever it says.
2. **Inbound (dispatch).** `ConversationManager` hand-dispatches on `functionCall.Name`
   and re-reads arguments by literal string key. The harness normalizes
   `(wireName, args) → (canonicalOp, canonicalArgs)` at the top of the tool-call loop,
   *after* the membership gate at `:527` (which must keep matching wire names, since
   those are what is in `requestOptions.Tools`). Every dispatch arm below then works
   unchanged.

Where a costume tool has no canonical 1:1 equivalent — `MultiEdit` being the obvious
case — the harness owns the decomposition into repeated canonical edits rather than
inventing a new nb-level operation.

### Selection

A program directive, beside `provider` / `model` / `tools` in `ProgramParser.cs:44-56`:

```
harness claude-code
```

It belongs in the program because the interesting experiment is *one model across two
harnesses*, and that has to be expressible as two files in a directory. Putting it in
`appsettings.json` forces mutating global config between runs — exactly what the
composable-CLI reorientation exists to prevent.

Provider config may still supply a default, for the repair case (a model that simply
works better in its native costume, regardless of what any given program is measuring).

### The preamble arrives with the costume

`harness codex` injects the Codex preamble. No second directive, no opt-in flag. A user
who asks nb to pretend to be Codex and is then told "of course it didn't work, you
never *explicitly* requested the system prompt" has been failed by the tool. Opting into
a costume opts into the whole costume.

`NbHarness` — the default, and whatever you get by not naming a harness — contributes
nothing. Bring your own system prompt, because nb has no idea what you are trying to do.
That is the same bare surface as today.

**This does not break §5.5.** The invariant at `docs/conversation-program-cli.md:257`
("No implicit persona. A program gets exactly the `system` directives it writes.")
protects two things: nothing arrives that the program did not ask for, and a reader can
predict behaviour from the program text. `harness codex` is an explicit request, visible
in the program, one word instead of two hundred lines. Restate the invariant as
**persona arrives only when the program asks for it — by `system` or by `harness`** and
it holds unchanged, with the bare default fully preserved.

**The transcript is what makes this safe.** `TranscriptEvent.cs:53` already establishes
that a system message round-trips like any other and "the system prompt" is not
special-cased. So the preamble materializes into the transcript as an ordinary `system`
event when the harness is folded in. Consequences worth having:

- A run captured under `harness codex` and replayed with `--seed` reproduces exactly,
  because the preamble is *in* the transcript rather than re-synthesized from a costume
  that may have been edited since. The costume prompt is versioned by the run that used
  it.
- "You didn't ask for it" never becomes "you can't see it." Everything the model was
  sent is on the wire record.

**Ordering:** harness preamble first, then the program's own `system` directives. The
program gets the last word, which matches how these harnesses layer project context
onto their own prompts anyway.

**Not building an escape hatch yet.** "Codex's tools with my prompt" is not expressible
under this design, and one real case wants it: the ablation that separates the
tool-surface variable from the prompt variable — which is precisely the open question at
the bottom of this plan about how much surface fidelity is load-bearing. That argues for
a `harness codex --no-prompt` form *eventually*, driven by someone actually running the
ablation. It does not argue for shipping the flag before then, and it must not soften the
default.

### Vocabulary

Programs speak the costume's names. Under `ClaudeCodeHarness` a program writes
`tools -Edit`, not `tools -edit_file` — because under that costume there genuinely is
no `edit_file`; there is `Edit` and `MultiEdit` and no clean mapping back.

Canonical identities survive *internally* for approval, `TrustSandbox` and
`FileReadTracker`, which must not learn that costumes exist. `ToolSurface.AllowsNative`
(`Transcript/ToolSurface.cs:21`) currently compares against canonical strings and will
need to resolve through the harness's vocabulary.

This is the invasive part of the change and the one most worth deciding deliberately;
it is what the dialects plan avoided by keeping everything canonical, at the cost of
programs whose text disagrees with their own transcripts.

## Compliance with `rules/model-policy-in-prompt-layers.md`

That rule says per-model behaviour lives in prompt layers, not engine control flow, and
that the engine carries **no conditional branch keyed on a model or provider identity**
(its canary: the engine has zero such branches today — keep it zero). Costumes as C#
classes could be read as hostile to it. They are compatible, under two constraints that
are part of this design rather than caveats on it:

1. **A harness is never inferred.** It is selected by an explicit `harness` directive
   and nothing else. No branch on model slug, no "this looks like a qwen model so wear
   the qwen costume." The rule bans `if (model.Contains("qwen"))`; a program that *says*
   `harness qwen-code` is a user declaration, not an engine inference, and the canary
   stays at zero. Provider config may name a default harness — the same sanctioned
   move as loading a policy file by slug — but the engine still branches only on the
   resolved harness object, never on identity strings.

2. **Preamble text is markdown, not C# string literals.** Costume prompts ship as data
   files loaded like the layered `system.*.md` files, with the class pointing at one.
   This keeps clause 2 honest for the half of the costume that is genuinely policy, and
   leaves the class carrying only schema shape and result formatting — mechanism, which
   is where code belongs. It also means a preamble can be revised without a rebuild,
   which matters given these are paraphrases tracking moving upstream targets.

Under those, the engine's turn loop stays uniform across every costume, and a harness is
the same category of thing as a prompt layer: data the engine consumes, not a branch
inside it.

## Prerequisite: fake tools need real schemas

`FakeToolManager.CreateAIFunctionFromFakeTool` (`MCP/FakeToolManager.cs:163-190`)
builds the declared parameters into the *description prose* and then registers the
function as `(IDictionary<string, object?> parameters) => response`. The reflected JSON
schema is therefore a single opaque `parameters` object — the declared parameters never
reach the wire.

For fake tools as a testing convenience this is tolerable. For harness emulation it
defeats the entire purpose, since the schema is the strongest signal in the whole
scheme. Emitting a real schema from `FakeTool.Parameters` is small, self-contained, and
independently useful; treat it as a prerequisite.

It matters because **a costume's tools do not all have to be real** — this is tier 2 of
the fidelity gradient above. Claude Code's surface includes `TodoWrite`, `Task`,
`WebSearch`, `NotebookEdit`, `Skill`; nb backs almost none of them. Those need to
*exist, be callable, and return something plausibly shaped* — which is precisely what a
fake tool is. Composing fake and real tools into one advertised surface is the natural
implementation, and the harness class is where that composition is declared.

## The four costumes

| Costume | Prompt licence | Notes |
|---|---|---|
| `QwenCode` | **Apache-2.0** — vendor | Subsumes the whole of `plans/tool-dialects.md`: `edit`, `glob`, `grep_search`, `list_directory`, `run_shell_command`, `web_fetch`, and `file_path` throughout. The one costume with a measured failure and a fixture behind it. |
| `Codex` | **Apache-2.0** — vendor | Partly exists already — see below. `apply_patch` plus a smaller surface. |
| `Cursor` | closed — paraphrase | Least researched surface, and the flakiest control (`cursor-agent -p` has reported hangs). Closed binary, so implementation confounds are opaque. Schedule last on those grounds, not on legal ones. |
| `ClaudeCode` | closed — paraphrase | Largest surface gap. `MultiEdit` needs decomposition; `TodoWrite` maps onto nb's existing todo tool; `Task` / `Skill` / `NotebookEdit` are fake-backed. Result formatting (line-prefixed reads, edit acknowledgments) is the high-value part. |

## Sourcing the preambles

Tool surfaces are public and documented — that half is research, not reverse-engineering,
and it is the half the "replicate the shape" line already covers. The prompts are the
hard half, and the difficulty turns out to be **per-costume, not uniform**.

### Two of the four are licensed

- **qwen-code is Apache-2.0**, being a fork of Gemini CLI (also Apache-2.0).
- **openai/codex is Apache-2.0.**

Their prompts can be vendored verbatim with attribution. Apache-2.0 material inside an
MIT project is routine: retain the licence header on the file, note any modifications,
and carry a third-party-licences entry. The project as a whole stays MIT; those files
carry their own terms. This composes exactly with the decision that preambles are
markdown data files — a vendored file with a licence header in a prompts directory *is*
that shape.

Cursor and Claude Code are closed. Those two are paraphrase-only, written from published
vendor documentation and observed behaviour.

### Not from OpenCode, and not from model recall

OpenCode routes per-model prompt files (`packages/opencode/src/session/prompt/` —
`anthropic.txt`, `beast.txt`, `gemini.txt`, `qwen.txt`), which makes it a tempting proxy.
It is not one. The current file is named `anthropic-20250930.txt` and references
`claude-sonnet-4-5-20250929`; a date-stamped filename tracking a model release is the
signature of a captured snapshot, not authored prose. OpenCode's MIT licence relicenses
only what OpenCode owns, so an MIT header over third-party prompt text cures nothing.

The same objection kills "ask a recent model to reconstruct the prompt": it launders the
copying through a model instead of a repo, and produces output that *looks*
independently authored while being a lossy reproduction of the expression we chose not
to copy. It is also unreliable — model priors on system prompts are contaminated by the
many fake "leaked prompt" repositories in training data.

Anthropic has a takedown precedent against Claude Code reverse-engineering specifically,
which makes that the costume to be most careful with, not least.

**Practical hygiene:** do not pull suspect prompt text into a working context at all.
Text that gets read gets paraphrased-from, and paraphrasing from the thing you were
avoiding copying is how it ends up in the repo anyway.

### Calibrating against the real prompt

`QwenCode` allows a direct check: the real prompt *and* a fixture with measured numbers.
Write a paraphrase of a prompt legitimately in hand, run both, diff behaviour. That
measures what a paraphrase costs against a known original.

It is the weaker of the two calibrations available, though — see *Controls* below, which
measures the thing we actually care about and applies to every costume.

## Controls — every costume has a real harness to diff against

**Prompt fidelity was never the objective; behavioural fidelity for a few turns was.**
Every target harness ships a headless mode, so the real thing can be run as a control on
the same fixture:

| Costume | Control |
|---|---|
| `ClaudeCode` | `claude -p --output-format stream-json` |
| `Codex` | `codex exec` |
| `QwenCode` | non-interactive mode (inherited from the Gemini CLI base) |
| `Cursor` | `cursor-agent -p --output-format json` — works, but `-p` has been reported hanging in some builds; treat as the flakiest control |

Three consequences, and they matter more than anything else in this plan:

1. **`ClaudeCode` becomes the best-validated costume despite having the worst prompt
   provenance.** Those two facts are unrelated. Closed prompt does not mean unmeasurable
   costume.
2. **Paraphrase becomes optimisation against a loss function**, not guesswork. Iterate
   the prose until the behavioural diff closes.
3. **The defensible method is also the effective one.** Writing prose to fit *observed
   behaviour* is independent authorship; writing prose to approximate *remembered text*
   is the thing being avoided. A control makes the first strictly better than the second,
   so there is no tension left between the legal constraint and the quality goal.

### Rule: no costume without a control

Do not build a costume whose real harness cannot be run headless on the fixture. An
unvalidatable costume is a placebo with no way to know whether it works, which is worse
than nothing — it produces confident-looking comparative numbers with no basis. All four
current targets pass this gate; it exists to filter the fifth.

### What to diff

The metrics `bugs/Tool_Names_Diverge_From_Model_Native_Surface.md` already collected for
qwen are the right ones: tool-choice distribution, call sequence, edit-vs-rewrite ratio,
turns to completion, completion/exit reason. Token counts are the weakest signal — that
report's own correction found input tokens moved <4% while useful work per token changed
sixfold.

### Confounds to build in from the start

- **Implementation delta is irreducible.** The real harness runs its own tools; nb's
  differ in edge cases regardless of how the surface is declared. Expect a floor on the
  diff and do not chase it to zero.
- **Context furniture is part of the harness**, which is why it is owned rather than
  optional (see *What a harness owns*, item 4). Replicate the channel shape per the
  tiering above; the tier-3 omissions are declared, so a diff has somewhere to point.
- **The control moves.** Vendors ship. Every control run must record the harness version
  alongside the model, or a re-run six weeks later silently compares against a different
  target.
- **Controls cost money and are rate-limited.** Fixtures should be small enough to re-run
  often.

## What this retires

- **`plans/tool-dialects.md`** — superseded. `QwenCodeHarness` covers its committed
  scope and is strictly more capable.
- **`EditToolStyle`** — `EditToolStyle: ApplyPatch` (`NbRuntime.cs:73-81`) swaps
  `edit_file` + `write_file` for `apply_patch`, and `apply_patch` *is* the codex-cli
  edit surface. That config field is a proto-harness that predates the idea.
  `CodexHarness` subsumes it. Deprecate rather than leave a second, weaker way to say a
  fraction of the same thing — but only once `CodexHarness` lands, and with a
  deprecation window, since it is documented in `CLAUDE.md` and `appsettings.example.json`
  and is presumably in use.

## Future door: runtime-loaded harnesses

The provider plugin architecture is the obvious template — publish an interface, sweep
implementations in from a directory at runtime via `AssemblyLoadContext`. Nothing in
this design forecloses it: the costumes are already independent classes with a narrow
surface, so extracting `IHarness` into an abstractions assembly later is mechanical.

Two things to keep in mind so the door stays cheap to open, without building toward it
now:

- **Keep the harness's outward surface narrow.** Tools in, result strings out, a
  preamble string. The moment a harness needs to reach into `ApprovalPolicy` or
  `FileReadTracker` it stops being extractable.
- **The ALC gotcha is worse here than for providers.** `CLAUDE.md` warns that shared
  types across `AssemblyLoadContext` boundaries cause type mismatches, and advises
  keeping provider communication to `IChatClient`. A harness hands back `AIFunction`
  instances and `JsonNode` schemas — considerably more type surface crossing the
  boundary than one interface. If this door is ever opened, that is the problem to
  solve first, and it may argue for harnesses shipping as *data plus a thin loader*
  rather than as arbitrary code. Which is worth knowing now: the in-tree classes should
  avoid gratuitous cleverness that a data representation could not later carry.

## Staging

0. **Characterisation test first.** `ConversationManager` is 2286 lines and its tool
   assembly (`:330-403`), dispatch loop (`:527-800`) and `GetAvailableTools` (`:2143`)
   have **no unit coverage** — the 355 existing tests reference `ConversationManager`
   only incidentally, and `ToolSurfaceTests` covers the `ToolSurface` type rather than
   the assembly that consumes it. So a green suite does *not* demonstrate that step 1
   preserved behaviour.

   Build the oracle before the refactor: capture the fully-assembled advertised surface
   (every tool name, description and emitted JSON schema) as a golden master, across the
   `tools` directive combinations that exercise the assembly's branches. This is cheap,
   it is the only thing that makes step 1 verifiable in place, and it is reused by every
   costume afterwards to assert what that costume advertises — so it pays for itself twice.

   **Done** — `nb.Tests/ToolSurfaceGoldenTests.cs` + `nb.Tests/golden/`. Five variants:
   all-native, `tools none`, a filtered subset, `EditToolStyle: ApplyPatch`, and
   `--nobash`. Captured through a real `RunAsync` against a recording `IChatClient`
   rather than through `GetAvailableTools()` — snapshotting the hand-maintained mirror
   would have proved nothing about what was actually advertised. Environment
   interpolation (shell cwd/name in `bash`, "Paths are relative to:" in the file tools)
   is scrubbed so the files are machine-independent. Re-baseline with
   `UPDATE_GOLDEN=1 dotnet test`.

1. Extract `NbHarness` from `ConversationManager`'s assembly block; no behaviour change.
   Verified by the step 0 golden master being byte-identical, not by the suite being green.

   **Done** — `nb.Core/Harness/NbHarness.cs`. The constructor lost ten positional tool
   parameters for one harness; dispatch arms keep concrete tool fields, mirrored out of
   the harness at construction, so canonicalisation can revisit them later without
   inflating this diff.

   `GetAvailableTools()` was **deleted, not collapsed**. It had no callers (the `/tools`
   command it served went with `CommandProcessor`) and had already drifted from the
   assembly it claimed to mirror — it ignored the tool surface for every native tool
   except todo, skipped `search_web`, and listed MCP tools unfiltered by server.
   `ToolDescriptor` existed only to feed it and went too. Anything needing that view
   should be rebuilt from `NbHarness.CreateTools`, which cannot drift by construction.
2. Fake-tool schema fix.
3. `harness` directive + transcript field; `NbHarness` remains the only value. Restate
   §5.5 and the §6 invariant in `docs/conversation-program-cli.md` at this step, while
   the restatement is still a no-op — the bare default is unchanged, so the wording
   lands before any costume can be accused of having quietly moved it.
4. `QwenCodeHarness` — first because it has a measured failure to test against *and* an
   Apache-2.0 prompt to vendor, so the surface half can be evaluated against a
   known-good prompt half rather than against two unknowns at once.
5. Build the control rig: run the real harness headless on the same fixture and diff the
   metrics above. Do this at `QwenCode` rather than later — the rig is reused by every
   costume, and it is what makes the remaining three tractable.
6. `CodexHarness` (also vendorable), then `ClaudeCode`, then `Cursor`. Ordering is now by
   control quality and surface-research cost, not by prompt licence — with a rig in
   place, a closed prompt stops being the binding constraint.
7. Deprecate `EditToolStyle` once `CodexHarness` exists.

## Verification

- **Behaviour-preservation (step 1)**: the step 0 golden master, byte-identical before
  and after. `dotnet test` staying green is necessary but *not* sufficient — the code
  being refactored is unit-test dark, so the suite cannot detect a regression in it.
  `ToolSurfaceTests`, `TranscriptSerializerTests`, `TranscriptMapperTests` and
  `TranscriptPorcelainWriterTests` hardcode canonical names and will catch leakage
  outward, which is a different and narrower guarantee.
- **Wire-level, no model needed**: run a program under each costume against the Mock
  provider with `--output jsonl` from `bin/Debug/net10.0` and assert on the advertised
  schema — that `ClaudeCode` advertises `Edit`/`file_path`, that a scripted call on the
  costume name dispatches and actually edits the file, that the transcript records the
  wire name and the harness. `Providers/Mock/MockProvider.cs:139` synthesizes arguments
  by tool name and needs cases for costume tools.
- **Live (qwen)**: `ssh imp '~/.local/bin/swap-model qcoder'`, run the edit-heavy
  fixture with and without `harness qwen-code`, compare edit success and completion.
  This is the measurement that decides whether the surface half is worth the prompt
  half's already-demonstrated 10× steer.
- **Placebo validation**: against the real harness run headless on the same fixture — see
  *Controls*. Behavioural tells (does it reach for `TodoWrite` unprompted, does it emit
  the characteristic idiom) are the qualitative read; the metric diff is the quantitative
  one. This is what turns "plausible placebo" from a hope into a claim.

## Open questions

- **Vocabulary is the live one.** Programs speaking costume names is the coherent
  choice but touches `ToolSurface`, the `tools` directive docs, and every fixture.
  Worth confirming before step 3.
- **How much result-formatting fidelity is actually load-bearing?** Ranked second here
  on reasoning, not measurement. Step 4's live run is the first chance to find out, and
  the answer should be allowed to reorder the staging.
