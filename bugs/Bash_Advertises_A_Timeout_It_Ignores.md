---
kind: bug
title: 'bash advertises `timeout_seconds` and ignores it'
created: 2026-08-14
updated: 2026-09-04
status: current
state: open
severity: low
cluster: schema-vs-dispatch
---

# bash advertises `timeout_seconds` and ignores it

Status: Open (2026-08-14) — found while deleting the qwen-code costume's argument
translation layer, which had been faithfully converting a value into a void.

## Symptom

`bash` advertises a `timeout_seconds` parameter and documents it in its own
description as *"Optional timeout (default {N}s)"*. A model that sets it gets the
default timeout regardless. No error, no warning — the argument is accepted and
discarded.

## Cause

Two halves that each look right on their own.

`BashTool.CreateTool` builds a lambda that wires the parameter correctly
(`nb.Core/Shell/BashTool.cs:50-51`):

```csharp
var executeFunc = (string description, string command, int? timeout_seconds) =>
    ExecuteAsync(command, null, timeout_seconds);
```

But native tools are **hand-dispatched** — that lambda is a declaration used to
reflect a name and schema, and is never invoked. The live path reads the arguments
itself and calls the tool directly (`nb.Core/Harness/NbHarness.cs:186, 514`):

```csharp
case "bash" when Bash != null:
    return await HandleBashToolCall(callId, Str(arguments, "command"), Str(arguments, "description"));
...
var result = await Bash.ExecuteAsync(command);      // no timeout argument
```

`ExecuteAsync` takes `int? timeoutSeconds = null` and falls back to the configured
default, so the omission is invisible.

## The general risk this is an instance of

**Any behaviour expressed in a native tool's `AIFunctionFactory` lambda is dead code.**
The lambda supplies the wire name and the reflected schema; nothing else about it runs.
That is deliberate — it is exactly what lets a harness costume advertise one shape over
another implementation — but it means a reader who checks "is this parameter wired up?"
by looking at the lambda gets a confident wrong answer, which is what happened here.

Worth auditing the other tools' lambdas for parameters that the hand-dispatch path does
not read. `read_file`, `find_files`, `grep` and `list_dir` were checked while extracting
them into capabilities and are complete; `apply_patch`, `fetch_url` and `search_web`
have not been.

## Why it matters

- A model asking for a long timeout on a slow build gets the default and a truncated
  run it cannot diagnose — the argument it set is simply gone.
- It is a fidelity problem for harness emulation: qwen-code's `run_shell_command`
  declares a real `timeout`, so the costume advertises one it cannot honour. The
  costume's declared omissions should say so until this is fixed.
- The same class of bug is what `bugs/Optional_Tool_Parameters_Advertised_As_Required.md`
  describes from the other direction: the advertised schema and the dispatch path
  disagree about what the arguments mean.

## Fix

Read `timeout_seconds` in the dispatch case and thread it through
`HandleBashToolCall` into `ExecuteAsync`:

```csharp
case "bash" when Bash != null:
    return await HandleBashToolCall(callId, Str(arguments, "command"),
        Str(arguments, "description"), Int(arguments, "timeout_seconds"));
```

The qwen-code costume then converts its own millisecond `timeout` to seconds at its
dispatch site — the conversion that existed in the old translation table, which was
dead for this same reason and was deleted with it.

## Verification

A program whose bash call sets a short `timeout_seconds` against a `sleep` that exceeds
it should return the timeout result rather than completing. Testable through the Mock
provider — `BuildToolArgs` already scripts `bash` — with no live model needed.

## Triage verification, 2026-09-04 — two findings that widen this

Verified against a fresh `dotnet build` at `0cb2567` + the uncommitted
`ConversationManager` string edits. Both findings mean the obvious one-line fix
(pass the argument through at the dispatch site) is **not sufficient**.

**1. The parameter is advertised as *required*, not optional.** The report and the
tool's own description both call it "Optional timeout". The emitted schema disagrees —
from `nb.Tests/golden/tool-surface.all-native.txt`:

```json
"timeout_seconds": { "type": ["integer", "null"] },
"required": [ "description", "command", "timeout_seconds" ]
```

So a model is obliged to supply a value on every `bash` call, and that value is then
discarded. This is `bugs/Optional_Tool_Parameters_Advertised_As_Required.md` and this
report meeting on the same parameter: one says it must be sent, the other says it is
ignored. Fixing only the dispatch wiring leaves it mandatory; fixing only the
optionality leaves it ignored.

**2. Even once wired, the value can only ever *lower* the timeout.**
`BashTool.ExecuteAsync` clamps (`nb.Core/Shell/BashTool.cs:84-85`):

```csharp
var requested = timeoutSeconds ?? _defaultTimeoutSeconds;
var timeout = Math.Min(requested, _defaultTimeoutSeconds);
```

`_defaultTimeoutSeconds` is the configured default, so `Math.Min` makes it a ceiling as
well as a fallback. A model asking for 600s on a slow build gets 120s. This is the
motivating case in "Why it matters" above — *"a model asking for a long timeout on a slow
build gets the default"* — and it survives the dispatch fix untouched.

Whether the clamp is deliberate (a model should not be able to hang a run for an hour)
or accidental is the open question. If deliberate, the honest fix is to stop advertising
a timeout the model cannot raise, or to advertise the real bound. If accidental, drop the
`Math.Min` and let the configured default be a default.

**Regression test.** Per `CLAUDE.md`, one hand-written assertion earns its place here
because it encodes a fact rather than a design: *a `timeout_seconds` above the configured
default must raise the effective timeout.* It is red today, stays red after the dispatch
wiring alone, and is the only assertion that catches the half-fix. The rest of this
report's surface (the `required` array) is pinned by the golden and needs no hand-written
test.
