---
kind: bug
title: 'Feature: the result trailer carries `program_sha256` and `nb_version`'
created: 2026-09-17
updated: 2026-09-19
status: current
state: fixed
severity: low
cluster: provider-truthfulness
---

# Feature: the result trailer carries `program_sha256` and `nb_version`

Status: Open (2026-09-17) — filed from proctor (`proctor/project/todo.md`, "Candidates
for nb, not proctor"; `learnings/prior-art.md`, "The decisions this makes obvious"), the
sidecar experiment manager being designed beside nb. Against `e34e762`.

## What is wanted

Two provenance fields on the trailer, always emitted like `provider`:

```json
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":10,"output":5,"total":15},"turns":1,"tool_calls":0,"provider":"Mock","program_sha256":"9f86d0…","nb_version":"0.9.0+e34e762"}
```

`program_sha256` is the SHA-256 of the **resolved program** — `TranscriptSerializer.Serialize`
(`TranscriptSerializer.cs:32`) over the event list `Nb.RunAsync` receives (`Nb.cs:31-34`) —
not of the source text. Decided that way because the facade never sees source: `@file`
includes are expanded at parse time (`ProgramParser.cs:243-247`) and a `--seed` prefix is
spliced into the same list (`Program.cs:325`, `:341`), so the resolved JSONL is what ran;
the source is a recipe for it. `nb_version` is the `AssemblyInformationalVersion` of the
assembly holding `Nb`.

## Why

A transcript found on disk a month later should tie itself to the exact program text and
binary that produced it. Today the transcript names the provider (and, once filed, the
model and cost) but not the program that drove it, and nb has no version at all. proctor
would otherwise hash programs and record `nb --version` in its own run record, which is
exactly the config-not-in-the-transcript drift `provider` was added to remove
(`TranscriptEvent.cs:294-300`).

**Always-on, not opt-in.** Same argument as `provider`'s doc comment: a reader cannot
distinguish "no hash because the program opted out" from "no hash because this is an old
nb". The eval and unit suites select trailer fields with jq filters
(`evals/run.sh:88-97`, e.g. `:187 has("oracle_turns")`, `:396 usage.total`) and the
golden trailer in `TranscriptSerializerTests.cs:21` is parsed, not diffed, so nothing
in-tree compares a whole trailer. No fixture update is needed.

## Additive guarantee

No existing field changes value or presence. `ResultEvent` is enrichment, ignored on
seed-load (`TranscriptEvent.cs:284`, `TranscriptLoader.cs:124`), so a replayed transcript
carries no trace of it. The honest caveat: a default trailer gains two keys, so it is not
byte-identical — the price of always-on, the same one `provider` paid.

## Where it lands

- `nb.csproj:8` and `nb.Core/nb.Core.csproj:7` both set `GenerateAssemblyInfo=false`, and
  neither has a `<Version>`, so today no informational version attribute exists to read.
  Add `<Version>` (a shared `Directory.Build.props` serves the release item too) and
  let the SDK generate the attribute, or hand-write it in `nb.Core/AssemblyInfo.cs`.
- `Nb.cs:89-103` — `RunResult` is assembled; hash `program` there, beside `Usage`.
- `TranscriptEvent.cs:285` — `ResultEvent.ProgramSha256`, `NbVersion`;
  `TranscriptMapper.ResultTrailer` (`TranscriptMapper.cs:98`) takes them; `WriteResultBody`
  (`TranscriptSerializer.cs:175`) writes them after `provider`; the reader at `:300-313` mirrors.
- `docs/conversation-program-cli.md:536-539` — the trailer field list.

## Test

Unit: `TranscriptSerializerTests` — a `ResultEvent` with both fields round-trips; the
golden example (no fields) still parses with nulls. Eval (`evals/run.sh`): the same
one-line Mock program run twice yields equal `select(.type=="result").program_sha256`,
and `nb_version` is non-empty; a program with an `@include` hashes the same as its
expanded form, proving the hash is over the resolved list.

## Open decisions

- Whether `--resolve` should print the hash so a program can be checked without running.
- Library hosts (`Nb.RunAsync` in-process): `nb_version` is nb.Core's, which is the
  engine that ran; the host's own version is its own business.

## Fix (2026-09-19)

Both fields, always emitted, hashing the resolved program exactly as the report
specified — `TranscriptSerializer.Serialize` over the event list `Nb.RunAsync` receives,
not the source text, because the facade never sees source and `@file` includes and a
`--seed` prefix are both resolved into that list before it arrives.

`nb_version` reads `NbVersion.Current`, shared with the `--version` flag
(`Feature_Version_Flag.md`) so the two cannot disagree — the convergence this report
asked for.

The version machinery landed with the flag. Two things differed from this report's
"Where it lands": `GenerateAssemblyInfo=false` had to be *removed* from the csprojs
rather than worked around, because `Directory.Build.props` is imported before the project
body and the per-project setting wins; and the SDK appends the commit sha to the
informational version on its own, so the hand-written `AssemblyInfo.cs` fallback this
report anticipated was unnecessary.

## Test

Eval: the same one-line program run twice yields equal `program_sha256`, a different
program yields a different one, and `nb_version` is non-empty.
