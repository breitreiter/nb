---
kind: bug
title: 'bash advertises `timeout_seconds` and ignores it'
created: 2026-08-14
updated: 2026-09-05
status: current
state: fixed
severity: low
cluster: schema-vs-dispatch
---

# bash advertises `timeout_seconds` and ignores it

Status: **Fixed 2026-09-05** — all three parts. Originally: Open (2026-08-14) — found while deleting the qwen-code costume's argument
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

---

## Fix (2026-09-05)

All three changes the triage verification called for, because any one alone leaves the
parameter broken in a different way.

**1. Dispatch reads the argument.** `case "bash"` passes `Int(arguments,
"timeout_seconds")` into `HandleBashToolCall`, which threads it to
`ExecuteBashCommand` and on to `ExecuteAsync`.

**2. The schema lets it be omitted.** Fixed by
`bugs/Optional_Tool_Parameters_Advertised_As_Required.md`, landed in the same commit —
`timeout_seconds` is off `bash`'s `required` array.

**3. The clamp is gone.** `Math.Min(requested, _defaultTimeoutSeconds)` became
`Math.Clamp(requested, 1, Math.Max(_defaultTimeoutSeconds, _maxTimeoutSeconds))`.

The open question in the triage note — deliberate or accidental — is answered
**deliberate in intent, wrong in mechanism**. Bounding what a model may ask for is
right: a headless run should not be parkable for an hour. Expressing that bound as the
*default* is what made the advertised parameter a lie, since it could only ever lower a
value the model had no reason to lower. So the bound survives as its own knob:

- `BashTimeoutSeconds` (default 120) is the **default**, used when the model asks for
  nothing.
- `BashMaxTimeoutSeconds` (default 600) is the **ceiling on what the model may request**.
- A configured default above the ceiling wins, because the ceiling exists to bound the
  *model*, not to overrule the operator. `ConfiguredDefaultAboveTheMaximum_IsHonoured…`
  pins that.

The tool description states the real bound: *"Optional timeout (default 120s, maximum
600s)"*. Advertising a limit the model can discover beats advertising a parameter it
cannot use.

**Costume conversion.** All three costumes declare their timeout in milliseconds
(`timeout_ms` on codex `shell_command`, `timeout` on claude-code `Bash` and qwen-code
`run_shell_command`) and each now converts at its own dispatch site via a shared
`MillisToSeconds` on the base — rounding *up*, so a sub-second request becomes 1s rather
than 0, which would have read as "nothing requested" and silently restored the default.
The conversion lives at the dispatch site rather than in the capability because it is a
property of the costume, which is what the deleted translation table got right about
*where* even while being dead code.

Their declared-omission strings were updated: timeout is no longer listed as accepted
and ignored, and the ms/seconds conversion plus nb's maximum are stated. Those strings
are model-visible, so this is a behaviour change to the costume surface, not bookkeeping.

**The audit this report asked for.** `apply_patch`, `fetch_url` and `search_web` were
checked and are clean — each takes a single required parameter that the dispatch path
reads. `apply_patch`'s lambda is a bare identity (`(string input) => input`), which
looks alarming and is correct: it exists only to carry the name and schema.

### Tests

`nb.Tests/BashTimeoutTests.cs`, 8 tests, **6 confirmed failing first** — on behaviour,
not on a compile error (the `maxTimeoutSeconds` parameter was added ahead of the clamp
change so the red would be real). The 2 that passed from the start assert behaviour that
was already correct: a requested timeout *below* the default still lowers it, and a
configured default above the ceiling is honoured.

The regression test the triage note specified —
`RequestedTimeoutAboveDefault_RaisesTheEffectiveTimeout` — is the one that catches a
half-fix, and did: it stays red after the dispatch wiring alone.
