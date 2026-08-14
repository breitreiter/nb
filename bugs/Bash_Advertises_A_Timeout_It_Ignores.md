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
