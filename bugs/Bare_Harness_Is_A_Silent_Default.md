---
kind: bug
title: A program that names no harness silently runs on nb's bare surface
created: 2026-09-09
updated: 2026-09-09
status: current
state: fixed
severity: high
cluster: harness-emulation
---

# A program that names no harness silently runs on nb's bare surface

Status: **Fixed 2026-09-09.** Originally: found after four consecutive runs staged as
"qwen code" turned out to be `harness=nb model=qwen`, costing an afternoon chasing
"model behaviour" that was only the missing costume.

## The bug

`harness` was optional and the fallback was `nb` — no preamble, no environment block,
no tool-choice steering, tool names nothing was trained on. A program that named a model
and forgot the costume ran, exited 0, and produced a trailer with no `harness` field at
all. Nothing in the run said it was bare. The odd behaviours that followed (whole-file
rewrites, guessed tool semantics, exhausted budgets) are documented consequences of
the bare surface (`docs/conversation-program-cli.md`, "Steering tool choice on the
bare surface"), but they look exactly like a model failing.

The registry already refused an *unknown* harness name on the argument that a silent
fallback "produces comparative numbers that mean nothing." A *missing* name got the
same fallback with no refusal.

## The fix

Every run must resolve a harness: program `harness` directive → `NbOptions.Harness`
(library hosts) → the active provider entry's `"Harness"` → top-level `"Harness"` →
refused with `HarnessRegistry.RequiredMessage` before any model call (exit 1). The bare
surface is `harness nb`, asked for by name. `--resolve` prints `harness=(none …)` for
the refused case and the inherited name otherwise.

Per-entry `Harness` is the plan's sanctioned config-default (`plans/harness-emulation.md`,
"A harness is never inferred": keyed by entry, never by model slug). The example config
pairs each entry with its vendor's costume, and the refusal text says the pairing out
loud, as guidance only: `claude-code` wants an Anthropic model, `codex` an OpenAI
model, `qwen-code` a Qwen model. Other combinations still run; they are experiments,
not "a test with Claude Code".

Regression: `nb.Tests/HarnessRequiredTests.cs` (seven cases, five observed red before
the fix) and the "harness is required" block in `evals/run.sh` (eight, six observed
red). Every test config and eval fixture now says `"Harness": "nb"`.
