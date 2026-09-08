---
kind: bug
title: 'A --seed composes with a program body only if the body uses inline `run` — any `user` line fails validation'
created: 2026-09-08
updated: 2026-09-08
status: current
state: open
severity: medium
cluster: transcript-replay
---

# A seed composes with a program body only if the body uses inline `run`

Status: Open (2026-09-08) — found while checking whether a run log can be
trimmed and resumed (`bugs/Feature_Resume_A_Run_From_Its_Log.md`), which needs
this to work.

## Repro

Any seed carrying more than one turn, plus a body with a standalone `user` line:

```bash
$ printf 'user keep going\nrun\n' | ./nb - --seed prior.jsonl --output jsonl
Error: turn 0 appears after turn 1: turns must be non-decreasing
$ echo $?
1
```

Nothing on stdout. The same seed with the documented inline form works:

```bash
$ echo 'run keep going' | ./nb - --seed prior.jsonl --output jsonl   # fine
```

`prior.jsonl` is an ordinary `--output jsonl` log — user at turn 0, assistant at
turn 1. Nothing about it is malformed; it loads by itself.

## Why

`--seed` prepends the parsed seed events to the program body and hands the
concatenation to one evaluator (`Program.cs:403`, `BuildProgramAsync`). But
`ProgramParser` numbers each program's turns from zero
(`ProgramParser.cs:35`), so the body's first message-bearing directive is turn
0 — arriving after the seed's turn 1. `TranscriptLoader.Validate` rejects the
batch on monotonicity (`TranscriptLoader.cs:72`) when the evaluator flushes.

The inline form escapes it because `run <text>` carries its prompt as a
`RunEvent`, and the prompt is appended straight to history in
`ConversationManager.RunAsync` rather than passing through the turn buffer. So
the seam is invisible for exactly the one shape the docs demonstrate (§7 shows
`echo 'run now finish it' | nb - --seed turn1.jsonl`), and fails for every other
way of writing the same program.

## Why it matters beyond the error message

The error is loud, which is the good case. Two things make it worth fixing
rather than documenting:

- **It bounds what a seed is for.** A seeded program cannot open with a `system`
  directive, cannot fabricate an `assistant` turn after the premise, and cannot
  build a multi-message turn before its `run`. All of those are ordinary program
  shapes; none of them are available with `--seed`. That is a much smaller
  feature than §7 describes.
- **The message names the wrong thing.** "turns must be non-decreasing" reads as
  a complaint about the seed file, which is where a reader will go looking. The
  seed is fine; the numbering of the *body* is what collided. Anyone debugging
  this starts by editing a file that has nothing wrong with it.

## Fix

Renumber the body's turns to continue from the seed's highest, at the point the
two are concatenated. The seed's own numbering is already validated on its own
terms, and the body's numbering is internal — nothing outside the parse depends
on it starting at zero.

Failing that, the error should at least say which side collided and that the
body is what gets renumbered.

## Note on the quieter case

Equal turn numbers pass the monotonicity check, and `ToHistory` then emits a
turn's events in role order regardless of the order they arrived in — system,
then user, then assistant, then tool (`TranscriptLoader.cs:33-56`). So a seed
whose final turn number matches the body's first can silently reorder the body's
`user` message ahead of the seed's `assistant` message instead of failing. It
needs a seed whose last turn carries an assistant message at turn 0, which an
ordinary log does not produce — but renumbering fixes both, and only renumbering
does.
