---
kind: bug
title: 'Every optional parameter on the native surface is advertised as required'
created: 2026-08-14
updated: 2026-09-04
status: current
state: open
severity: low
cluster: schema-vs-dispatch
---

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

## Scope correction, 2026-09-04 — native surface only; the costumes are already correct

Triage checked the `required` array of every tool in every golden surface. The defect is
**confined to nb's own native surface**. Every costume already declares optionality
correctly:

| surface | tools with a correct `required` array |
|---|---|
| `all-native` | 0 of 10 — every tool marks every parameter required |
| `apply-patch` | 0 of 9 |
| `qwen-code` | 6 of 9 (`read_file` requires only `path`; `edit` omits `replace_all`; `grep_search` omits `path`/`glob`/`limit`) |
| `claude-code` | 7 of 11 (`Grep` requires 1 of 12; `Read` omits `offset`/`limit`) |
| `codex` | 3 of 4 (`shell_command` omits `workdir`/`timeout_ms`) |

The remaining "all required" entries under the costumes are tools whose parameters
genuinely are all mandatory (`write_file`, `WebFetch`, `TodoWrite`), not instances of
this bug.

**Cause of the split.** The two surfaces build schemas by opposite mechanisms, with
opposite defaults:

- Costumes hand-declare via `SchemaBuilder.Add(name, type, description, bool required = false)`
  (`nb.Core/Harness/DeclaredFunction.cs:39`) — a parameter is optional unless the costume
  opts in.
- The native surface reflects the tool lambda through `AIFunctionFactory`, where a
  parameter is required unless it carries a C# default — and none do.

**One bullet above is now wrong and should be read as superseded.** "It blocks costume
fidelity … a harness costume cannot match its target's schema while the underlying tools
cannot express optionality" — costumes match their targets' schemas today, because
`DeclaredFunction` decouples the advertised schema from the lambda entirely. This bug
costs nb's *own* surface fidelity, not the costumes'. That lowers its urgency, and it is
why it is tagged `severity: low`.

The fix in the section above is unchanged and still correct — add C# defaults to the
lambda parameters — and `ToolSurfaceGoldenTests` re-baselining is still the review.
