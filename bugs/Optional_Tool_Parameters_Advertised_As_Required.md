# Every optional parameter on the native surface is advertised as required

Status: Open (2026-08-14) — found while building the tool-surface golden master
(`nb.Tests/ToolSurfaceGoldenTests.cs`), which prints the emitted JSON schemas and
made this visible for the first time.

## Symptom

Every native tool declares all of its parameters in the schema's `required` array,
including the ones documented as optional. From the golden files:

| tool | `required` includes | documented as |
|---|---|---|
| `read_file` | `offset`, `limit` | "default: 1", "default: 2000" |
| `bash` | `timeout_seconds` | "Optional timeout" |
| `edit_file` | `replace_all` | optional |
| `find_files` | `max_results` | optional |
| `grep` | `path`, `file_pattern`, `case_insensitive`, `max_results`, `output_mode` | all optional |

`grep` is the extreme case — six parameters, one genuinely required:

```json
"required": ["pattern", "path", "file_pattern", "case_insensitive", "max_results", "output_mode"]
```

## Cause

`AIFunctionFactory.Create` treats a parameter as optional only when the C# parameter
has a **default value**. No tool lambda declares one:

```csharp
// GrepTool.cs:25
var grepFunc = (string pattern, string path, string file_pattern,
                bool? case_insensitive, int? max_results, string output_mode) => …

// ReadFileTool.cs:27
var readFunc = (string path, int? offset, int? limit) => …
```

A nullable *type* (`int?`) is not the same as a defaulted *parameter*. `int? limit`
reflects as "required, may be null"; `int? limit = null` reflects as optional.

There is a second, related shape in the string parameters. `grep`'s `path`,
`file_pattern` and `output_mode` are declared as plain non-nullable `string`, and the
body treats empty string as "not supplied":

```csharp
Grep(pattern, string.IsNullOrEmpty(path) ? null : path, …)
```

So "optional" lives in the description prose and in an empty-string sentinel, while the
wire contract says mandatory.

## Why it matters

- **The model must emit every argument on every call.** Small per call, paid on every
  call, and `grep`/`read_file` are among the most-called tools.
- **It invites invented values.** A model obliged to supply `max_results` or
  `output_mode` picks something rather than omitting it, so a nb-specific schema quirk
  turns into a behavioural difference — the model narrows a search it never meant to
  narrow. This is worse than the token cost.
- **It confounds cross-model comparison**, which is the same argument as
  `bugs/Tool_Names_Diverge_From_Model_Native_Surface.md`: a model penalised here is
  being penalised for a property of the harness.
- **It blocks costume fidelity.** Both qwen-code and Claude Code declare these
  parameters genuinely optional. A harness costume (`plans/harness-emulation.md`) cannot
  match its target's schema while the underlying tools cannot express optionality —
  and schema shape is the highest-value rung in that plan.

## Fix

Add C# default values to the tool lambda parameters — `int? limit = null`,
`bool? replace_all = null`, and so on. Lambdas support default parameter values, so this
is a signature-only change; the bodies already handle null.

The string-with-empty-sentinel parameters (`grep`, `find_files`) want `string? path = null`
so the sentinel can go away, which changes the body slightly.

## Verification

`nb.Tests/ToolSurfaceGoldenTests.cs` already pins the emitted schemas. Re-baseline with
`UPDATE_GOLDEN=1 dotnet test` and the diff *is* the review: every `required` array should
shrink to the genuinely mandatory parameters, and nothing else in the golden should move.
