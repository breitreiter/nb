---
kind: rule
title: The persona floor is a preset on the bare human path — never injected into a program
created: 2026-07-12
updated: 2026-07-12
provenance:
  source: claude
enforces:
  - Program.cs
  - ProgramEvaluator.cs
---

# The persona floor is a preset, loaded only on the bare human/-p path

nb's default persona (the assembled `system.md` + shell-env + provider/model
prompt layers + NB.md) is a **floor for the interactive / bare-prompt path
only**. It is a preset that path opts into, not a thing the engine smuggles into
every run. This is the rc-file rule: `ssh host cmd` doesn't source `.bashrc`, and
`nb --program p.nb` doesn't get the persona.

## The clauses

1. **A program is never given an implicit persona.** The `--program` and
   `--spec` paths evaluate exactly the directives supplied — a bare program with
   no `system` directive produces **no system message**. This is what an eval
   harness needs: the thing under test is not silently wrapped in nb's assistant
   persona. (`ProgramEvaluator` appends only the program's own `system` events;
   `Program.cs` skips `InitializeWithSystemPrompt` on the program branch.)

2. **The persona comes from a preset, visibly.** On the bare human/`-p`/
   single-shot-prompt path, the persona is the default preset's directives. `nb
   -p "x"` gets the persona because the preset *carries* it; the directives are
   inspectable (`--resolve`), not conjured. "Missing prompt" is therefore
   impossible on the floored path, and its absence on a program is a deliberate
   choice, not a bug.

3. **`system` is a plain message, not a singular owned prompt.** There is no
   special-cased "the system prompt." A program may write zero or more `system`
   directives anywhere; they round-trip like any other message. Nothing is
   dropped or injected (retires the S2 "seed drops system messages" hack once the
   seed path routes through the evaluator).

## Status (2026-07-13)

All three clauses are realized.

Clause 1 is enforced: the `--program`/`--spec` branch is persona-free
(`Program.cs`, verified by the "bare program has no system message" eval).

Clause 2 is realized: the bare `nb "prompt"` path builds the persona as a
first-class directive list — `BuildDefaultPresetEvents` returns a
`List<TranscriptEvent>` (today one `SystemEvent`: base prompt + shell-env +
provider/model prompt layers + NB.md) — and evaluates it through the same
`ProgramEvaluator` as any program. `InitializeWithSystemPrompt` is gone; the
persona is no longer an engine-injected prompt but a preset routed through the
engine. The preset is also a *named* built-in the resolver hands back: `--spec
chat` returns the same computed `BuildDefaultPresetEvents` (a computed built-in,
not `BuiltInSpecs` text), so `nb "x"` and `nb --spec chat "x"` produce a
byte-identical persona and a program can opt into the floor explicitly (`nb
--spec chat --program p.nb`).

Clause 3 is realized: a `--seed` transcript's own `system` messages now survive
(`Program.LoadSeed` appends them as premise instead of dropping them with a
warning). They append after the default preset's persona, no different from any
other seeded turn. Verified by the "seed: own system message survives" eval
(count of system events = preset's 1 + seed's 1 = 2).
