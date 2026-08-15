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
  (`Cursor` is deferred — see *Staging* step 6. The set is still closed; it is just three.)
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

**Judgement, with the omissions written down.** Tiering is a judgement call, and the
thing that keeps it honest is not measurement but disclosure: every skipped channel is
declared, so a reader can see what was traded away and argue with it. A control can
promote a mis-tiered omission if you choose to run one, but the discipline does not
depend on that — it depends on nobody being able to skip something quietly.

**No clutter, either.** Omission is one failure mode; addition is the other, and it is
the easier one to talk yourself into. A costume must not advertise a tool its target does
not have — not nb-specific tools with no counterpart, not "harmless" extras, not a tool
kept because dropping it would break some other configuration. The goal is a model that
behaves as though it were in the target harness, not a model with a bigger toolbox, and
every extra tool is a behavioural difference you then cannot attribute.

This bit immediately: `QwenCodeHarness` initially passed `apply_patch` through under its
own name, rationalised in a code comment as avoiding a stripped edit tool under
`EditToolStyle: ApplyPatch`. qwen-code has no `apply_patch`. The right answer is to drop
it and report the configuration conflict — `EditToolStyle: ApplyPatch` builds `apply_patch`
*instead of* `write_file` + `edit_file`, so that entry genuinely cannot wear this costume,
and saying so beats shipping a surface nobody asked for. `ToolSurfaceGoldenTests` pins the
advertised set and `Costume_AdvertisesNothingQwenCodeDoesNotHave` asserts the rule
directly.

The proper fix is architectural and belongs with the `EditToolStyle` deprecation (step 7):
`NbRuntime` should always construct every tool and let the *harness* decide which to
advertise, rather than the runtime pre-deciding by building one and not the other. Then a
costume never inherits a hole from provider config.

**Done, at step 6** — `CodexHarness` forced it, since Codex needs `apply_patch` and would
otherwise have shipped inert unless a provider entry happened to ask for it. The
`CONFLICT:` omission is gone with it.

**What still reaches the model from outside the costume.** MCP tools and fake tools are
merged into the surface by `ConversationManager`, not the harness. For programs MCP is
already strict-empty unless a directive names servers, so it is opt-in; `fake-tools.yaml`
auto-loads from cwd, which is a file someone deliberately created. Both are legitimate but
both dilute a costume — a run measuring faux-qwen-code against real qwen-code should carry
neither.

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
| `Cursor` | closed — paraphrase | **Deferred indefinitely** (2026-08-14) — see below. Least researched surface, and the flakiest control (`cursor-agent -p` has reported hangs). Closed binary, so implementation confounds are opaque. |
| `ClaudeCode` | closed — paraphrase | **Built** (step 6). `MultiEdit` turned out to be retired upstream, so the decomposition this row predicted was never needed; `TodoWrite` maps onto nb's todo tool; `Task` / `Skill` / `NotebookEdit` are declared stubs that announce themselves. Result formatting (line-prefixed reads, edit acknowledgments) is the high-value part and is nb's already. |

## Sourcing the preambles

Tool surfaces are public and documented — that half is research, not reverse-engineering,
and it is the half the "replicate the shape" line already covers. The prompts are the
hard half, and the difficulty turns out to be **per-costume, not uniform**.

### Two of the four are licensed

- **qwen-code is Apache-2.0**, being a fork of Gemini CLI (also Apache-2.0).
- **openai/codex is Apache-2.0.**

Their prompts *may* be vendored verbatim with attribution. Apache-2.0 material inside an
MIT project is routine: retain the licence header on the file, note any modifications,
and carry a third-party-licences entry. The project as a whole stays MIT; those files
carry their own terms. This composes exactly with the decision that preambles are
markdown data files — a vendored file with a licence header in a prompts directory *is*
that shape.

**Being permitted to vendor is not a reason to.** qwen-code, done first, settled this in
practice (step 5): its prompt is not a file but a 76KB TypeScript module assembling text
by conditional interpolation, so vendoring would mean freezing one rendered variant and
presenting it as the source. An authored facsimile was smaller, honest about its own
provenance, and free of a licence obligation. Check the shape of the upstream prompt
before assuming the licence decides the question.

**And for Codex it did come out the other way** (step 6). Its prompt is a static
markdown file with no interpolation, so there was a real artefact to copy, and a copy is
more faithful than any facsimile could be. Vendored under Apache-2.0 with two stated
modifications. Two costumes, one licence, opposite decisions — the shape of the upstream
artefact is what decides, not its terms.

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

**And the case this project has that most do not:** nb's costumes are largely written by
an assistant that is itself running inside one of the target harnesses, with that
harness's real system prompt in its context. Transcribing it would be the most direct
copy available, not the most legitimate — the fact that the text is *right there* makes
it a worse source, not a better one. `ClaudeCodeHarness` was written without it, and the
prompt file's header says so, because a claim of independent authorship that nobody can
check is worth less than one that is written down and can be argued with. The tool
*surface* is a different matter: schemas observed first-hand are interface facts, and
that half is reproduced exactly.

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

### Controls are a tool, not a gate

Earlier drafts of this plan made a headless control a precondition for building a
costume. That was wrong, and it nearly turned a tooling project into an eval project.

The premise stands on its own: *if you want to know whether something works in Codex,
run it somewhere Codex-shaped.* That is face-valid and does not need a measured
behavioural delta to justify it. A costume's job is to **be a faithful environment**,
and fidelity is checked against the target's published surface — which
`ToolSurfaceGoldenTests` and `Costume_AdvertisesNothingQwenCodeDoesNotHave` already do,
without a model in the loop.

Run a control when you actually want to answer a behavioural question: does the costume
change what the model does, how much does a paraphrased prompt cost, is a tier-3
omission really harmless. Those are good questions. They are not the entry fee.

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
2. Fake-tool schema fix. **Done** — `FakeAIFunction` in `nb.Core/MCP/FakeToolManager.cs`
   subclasses `AIFunction` directly and builds the schema from the declared parameters,
   since it comes from data rather than a reflected signature. Author-friendly type
   spellings normalise; unknown types pass through. The dispatch-side unwrap of a nested
   `parameters` object went with it — pure compensation for the opaque schema, and a trap
   once schemas are flat. Covered by `nb.Tests/FakeToolSchemaTests.cs` (the manager had no
   tests before).
3. `harness` directive + transcript field; `NbHarness` remains the only value. Restate
   §5.5 and the §6 invariant in `docs/conversation-program-cli.md` at this step, while
   the restatement is still a no-op — the bare default is unchanged, so the wording
   lands before any costume can be accused of having quietly moved it.

   **Done** — `HarnessEvent`, `HarnessRegistry`, `ProgramEvaluator.Harness`,
   `RunResult.Harness`, `ResultEvent.Harness`. An unknown name is a **parse error with a
   line number**, breaking from the evaluator's warn-and-ignore treatment of unknown
   approval keys: an ignored approval key degrades safely, a silently-ignored costume
   invalidates the run's meaning. Docs updated at §5.1, §5.5, §6 and §10. Covered by
   `nb.Tests/HarnessDirectiveTests.cs`.
4. `QwenCodeHarness` — first because it has a measured failure to test against *and* an
   Apache-2.0 prompt to vendor, so the surface half can be evaluated against a
   known-good prompt half rather than against two unknowns at once.

   **Done (surface only)** — `nb.Core/Harness/QwenCodeHarness.cs`. The prompt is *not*
   vendored: qwen-code's lives in a 76KB TypeScript file with conditional interpolation,
   which is its own task, and paraphrasing prose we are licensed to copy verbatim would
   be the wrong trade. It is one of seven declared omissions, surfaced as run warnings.

   This also isolates a variable usefully. The bug report measured the *prompt* steer
   alone (a 10× swing in tool selection from one sentence); this measures the *surface*
   alone. Vendoring the prompt afterwards gives the third cell.

   Found while testing: nb's bash dispatch arm read `description`/`command` through the
   raw indexer, which throws on a missing key. Pre-existing, rarely fired because nb's
   own schema marks them required, exposed the moment a costume declared `description`
   optional. Fixed here.
5. Close the qwen-code costume's largest declared omission — its missing prompt — and
   build the preamble mechanism that every later costume reuses.

   **Done** — `NbHarness.Preamble` + `LoadPreamble`, a `Content` file at
   `nb.Core/prompts/harness/qwen-code.md`, and injection in `ProgramEvaluator.ApplyHarness`
   as an ordinary `SystemEvent` at the front of the pending turns. Covered by
   `nb.Tests/HarnessPreambleTests.cs`, which asserts off a recording `IChatClient` — what
   went on the wire, not what the evaluator thinks it buffered.

   **Authored facsimile, not vendored** — a change from what this step originally said,
   and the reasoning is worth keeping. qwen-code's prompt is Apache-2.0 and could be
   copied verbatim with attribution, but it is not a file: it is a 76KB TypeScript module
   that assembles text by conditional interpolation (sandbox on/off, git repo or not,
   tool names spliced in). "Vendor the prompt" would mean choosing one rendered variant
   and shipping it as though it were the source. An nb-authored facsimile occupying the
   same channel — role, conventions-first mandates, plan/implement/verify workflow, terse
   CLI output contract, the edit-over-rewrite steer — is smaller, stays a data file
   rather than a template engine, and carries no third-party licence obligation into an
   MIT repo. The cost is that wording differences from the real prompt are unmeasured,
   which is now what the costume's `system prompt:` omission says.

   The preamble lives in `nb.Core`, not beside the CLI's `prompts/system*.md`, so a
   library host calling `Nb.RunAsync` gets it too.

   Found while testing: two concurrent `RunAsync` calls in one process collide on
   Spectre's global `AnsiConsole`, and the loser returns having never called the model.
   Filed as `bugs/Concurrent_Runs_Collide_On_The_Global_Console.md`; worked around in the
   tests by an xunit collection.
6. `CodexHarness` (also Apache-2.0, also vendorable), then `ClaudeCode`, then `Cursor`.
   Ordering is by prompt licence and surface-research cost. Building a control rig is
   optional at any point and gates nothing. **Codex and ClaudeCode are done; Cursor is
   deferred indefinitely — this step is closed at three costumes.**

   **Codex done** — `nb.Core/Harness/CodexHarness.cs`, four tools: `shell_command`,
   `apply_patch`, `update_plan`, `view_image`. Schemas verified against `openai/codex`
   at `codex-rs/core/src/tools/handlers/*_spec.rs`.

   **Vendored, and that is the contrast with qwen.** This step's prediction held: Codex's
   prompt *is* a file (`codex-rs/core/gpt_5_2_prompt.md`, static markdown, no
   interpolation), so there was a real thing to copy and the licence permits copying it.
   It is vendored with an Apache-2.0 notice and two stated modifications — the model
   identity is generalised, and the clause declaring `apply_patch` a freeform tool is
   dropped, since nb has no freeform tool channel and the sentence would steer the model
   into emitting a bare patch where an argument object is required. So the two costumes
   built so far went opposite ways on the same licence, decided by the shape of the
   upstream artefact rather than by its terms.

   **Subtraction is the costume.** Codex has no read, write, edit, glob, grep or list
   tool — it greps with `rg` and reads with `sed -n` through the shell. Building this one
   was mostly *withholding* seven tools nb has, which is a larger behavioural change than
   any renaming the qwen costume does, and it is the first real test of the no-clutter
   rule.

   Two things fell out of that and are worth keeping:

   - **`EditToolStyle` had to stop reaching costumes first** (see step 7). Codex needs
     `apply_patch`, which the runtime only built when a provider entry asked for it, so
     the costume would have been inert by default — exactly what the composable-CLI
     reorientation exists to prevent.
   - **AGENTS.md, the first context-furniture channel.** Item 4 of *What a harness owns*
     is now real for one costume: `NbHarness.ProjectInstructions()` is a virtual the
     evaluator injects right after the preamble, and `CodexHarness` fills it by walking
     the project root down to the cwd for `AGENTS.override.md` / `AGENTS.md`, bounded at
     Codex's own 32 KiB, wrapped in Codex's own `# AGENTS.md instructions for <dir>` +
     `<INSTRUCTIONS>` block (verified against `codex-rs/core/src/agents_md.rs` and
     `context/user_instructions.rs`). Discovery is shared on the base class, so the
     CLAUDE.md and QWEN.md channels are a filename and a wrapper away.

     One consequence to state plainly, because it is a genuine loosening: **a named
     harness makes a program directory-dependent.** The same program run in two
     repositories now sends different text. That is what the imitated harness does, so
     it is right, and the transcript still records everything that went on the wire — but
     "predict the run from the program text" now means *program plus cwd* under a
     costume. §5.5 is restated accordingly.

     The one deliberate infidelity: Codex sends this as a user-role fragment and nb sends
     a system message, because consecutive user messages are not portable across
     providers. The wrapper carries the framing either way. Declared.
   - **Read-before-edit is unsatisfiable without a read tool.** nb refuses an edit to a
     file it never saw read; under this costume the model reads through the shell, which
     `FileReadTracker` cannot observe, so every patch would have been refused. The
     tracker gained `RequireReadBeforeEdit`, which the costume turns off — matching the
     real harness. Approval and the trust sandbox are untouched. This is the first case
     of a costume needing to relax an nb safety mechanism, and the general shape (the
     mechanism is coupled to nb's *tool set*, not to safety as such) will recur.
   **Claude Code done** — `nb.Core/Harness/ClaudeCodeHarness.cs`. Eleven advertised
   tools, `CLAUDE.md` in a `<system-reminder>` wrapper, and the widest gap between the
   provenance of its two halves.

   **The surface is exact; the prompt is authored.** Names, parameter spellings, enum
   values and Grep's ripgrep-shaped flags are interface facts, reproduced as-is. The
   prompt is an nb-authored facsimile, because Claude Code's is closed and there is a
   takedown precedent. Three sources were ruled out and the file's header records all
   three, including the one this project had uniquely available and did not use:
   transcription by an assistant that is itself running as Claude Code, with the real
   prompt in its context. That is the most direct copy of the three, not the most
   legitimate. Writing to *observed behaviour* stays the method.

   **Corrections to this plan, found by building it:**

   - **`MultiEdit` is gone from Claude Code.** The table below still lists decomposing it
     as the hard part; it is not, because `Edit` absorbed `replace_all` and `MultiEdit`
     was retired upstream. The costume does not advertise it, and a test asserts so.
   - **Not every tier-2 tool should be faked.** `BashOutput` / `KillShell` exist on the
     real surface, but nb's bash has no background mode, so a model that starts a
     background command and then polls forever is *worse off* than one that never
     backgrounds. They are omitted and declared. Presence is not unconditionally the
     right call — the test is whether the model can get stuck in the gap.
   - **A stub must announce itself.** `Task`, `Skill` and `NotebookEdit` are declared and
     return an explicit "not implemented, nothing ran, do not retry". Returning a
     plausible fake would put a fabricated result into the transcript and make the run
     silently meaningless, which is precisely the unfalsifiable-placebo failure this plan
     exists to avoid.
   **Cursor deferred indefinitely** (2026-08-14). Not cancelled and not blocked — there
   is nothing stopping it being written, and the class would slot in beside the other
   three unchanged. It is simply not worth building next. Three costumes cover the
   harnesses this project actually compares against; Cursor is the least-researched
   surface, behind a closed binary whose implementation confounds are opaque, with the
   flakiest control of the four. Its marginal value is below **result formatting**, which
   is unbuilt for *all three existing costumes* and which this plan ranks second in what
   a harness owns. Fixing that improves three costumes at once; adding Cursor improves
   none of them.

   Revisit if someone actually needs to test something in a Cursor-shaped environment.
   That is a real reason and it would move this straight back up.

   - **`tools` cannot filter a tool nb has no name for.** The three stubs have no
     canonical counterpart, so they ride the surface as a group: present by default, gone
     under `tools none`. Finer control would mean growing nb's vocabulary with names for
     tools nb does not have, which is the wrong trade. Declared.
7. Deprecate `EditToolStyle` once `CodexHarness` exists.

   **Architectural half done** (with step 6, because Codex forced it). `NbRuntime` now
   builds `write_file`, `edit_file` *and* `apply_patch` whenever tools are wired, and
   `EditToolStyle` survives only as `NbHarness.ApplyPatchStyle`, which picks which pair
   nb's own surface advertises. Costumes ignore it. A costume can no longer inherit a
   hole from provider config, which retires the qwen costume's `CONFLICT:` omission
   outright. Verified by the step 0 golden masters being byte-identical.

   The config field itself is still there and still documented; deprecating it is a
   user-facing change with a window, and is what remains of this step.

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
