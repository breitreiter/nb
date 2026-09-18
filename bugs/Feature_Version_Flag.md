---
kind: bug
title: 'Feature: a `--version` flag that prints nb''s informational version and exits'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: low
cluster: cli-surface
---

# Feature: a `--version` flag that prints nb's informational version and exits

Status: Open (2026-09-17) — filed from proctor, the sidecar experiment manager that
spawns nb as a subprocess and wants to record which nb it ran. Related:
[`Feature_Trailer_Carries_Program_Hash_And_Nb_Version.md`](Feature_Trailer_Carries_Program_Hash_And_Nb_Version.md)
asks for `nb_version` on the trailer, which only tells a reader the version *of a run
that happened*; this asks for the flag, which answers "what would run" without running a
program at all. Against `e34e762`.

## What is wanted

`nb --version` prints the informational version to stdout and exits 0, without treating
the flag as a program file.

## Symptom

`--version` is not a recognised flag, so it falls through to the positional-argument
path and is treated as a program file:

```
$ bin/Debug/net10.0/nb --version
Error: program file not found: --version
```

(`Program.cs:336`: `throw new FileNotFoundException($"program file not found:
{_programFile}");` — `_programFile` is set from the first unrecognised positional
argument at `Program.cs:122`.)

## Why

A tool that spawns nb and records provenance — proctor's run record, or any harness
doing the same — has no way to ask "which nb did I just run" without running a program.
Today proctor falls back to `FileVersionInfo` of `nb.dll`, which reads `1.0.0` because no
csproj sets a version at all:

```
$ grep -n GenerateAssemblyInfo nb.csproj nb.Core/nb.Core.csproj
nb.csproj:8:    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
nb.Core/nb.Core.csproj:7:    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
```

Both projects turn off assembly-info generation and neither defines `<Version>`, so
there is no informational-version attribute to read even by the fallback path —
`FileVersionInfo` is reading whatever the SDK's default happens to be, not anything this
repo chose.

## Fix

Two independent parts, both already anticipated by the sibling trailer report's "Where
it lands" section:

1. **A version to report.** Add `<Version>` to `nb.csproj:8` and
   `nb.Core/nb.Core.csproj:7` (a shared `Directory.Build.props` would serve both, plus
   the trailer feature, in one place) and let the SDK generate the informational-version
   attribute despite `GenerateAssemblyInfo=false` disabling the rest of it — or hand-write
   it in a small `nb.Core/AssemblyInfo.cs` if the SDK's interaction with that flag is
   awkward.
2. **The flag.** In `Program.ParseFlags` (`Program.cs:33-99`), recognise `--version`
   alongside `--help`/`-h` (`Program.cs:91-93`) and print `AssemblyInformationalVersion`
   of the assembly holding `Nb.RunAsync` (nb.Core, the engine that actually ran) then
   return before the "no program, no `--validate`/`--resolve`" exit at `Program.cs:177`
   — the same short-circuit shape `_showHelp` already uses at `Program.cs:145-149`.

## Test

A `--version` invocation exits 0 and prints a non-empty version string to stdout, with no
attempt to read a program file. Once `<Version>` is set, the printed string should be
non-`1.0.0` and match the value `nb_version` would carry on a trailer per
`Feature_Trailer_Carries_Program_Hash_And_Nb_Version.md`, so the two reports converge on
one version, not two independently-chosen ones.
