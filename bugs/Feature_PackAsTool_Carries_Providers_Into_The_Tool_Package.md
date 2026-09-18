---
kind: bug
title: 'Feature: a `PackAsTool` target that carries `providers/` into the tool package'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: low
cluster: distribution
---

# Feature: a `PackAsTool` target that carries `providers/` into the tool package

Status: Open (2026-09-17) — filed from proctor (`proctor/project/todo.md`, "Candidates
for nb, not proctor"; `learnings/ci-distribution.md`, "Where nb is today", ".NET
gotchas", "What this adds to the design"). Against `e34e762`.

## What is wanted

`dotnet pack nb.csproj -c Release` produces a .NET tool package that, once installed,
runs with every provider present:

```bash
dotnet tool install --tool-path /tmp/tp --add-source ./nupkg nb
echo 'run MOCK:response=hi' | /tmp/tp/nb - --output jsonl --config evals/test-appsettings.json
```

Framework-dependent, untrimmed, not single-file. Consumers then pin nb in a
`dotnet-tools.json` manifest and `dotnet tool restore` instead of cloning and building.

## Why

The only install paths today are build-from-source (README.md:81-92, "nb must run from
the bin directory") and unsigned binaries from a releases page (`:96`) that no workflow
populates. proctor wants a consumer's CI job to be a manifest and `dotnet tool restore`
(`ci-distribution.md`, "The recommendation"), which every well-known .NET CLI does and
which needs no consumer auth. A tool package packs the publish output, so this is
mostly making the existing provider copy fire on the publish that `pack` runs.

## Additive guarantee

`dotnet build`, `dotnet test`, `evals/run.sh` and the per-RID `dotnet publish` in
`ci.yml:36-38` are untouched; the pack path is a new invocation. Nothing at runtime
changes: providers still load from `AppContext.BaseDirectory/providers`
(`ProviderManager.cs:20`), which inside a tool install is the package's own content dir.

## Where it lands

- `nb.csproj:97-117` — `BuildProvidersForPublish` and `CopyProvidersAfterPublish` are
  both conditioned on `'$(RuntimeIdentifier)' != ''`. A RID-less pack publish never runs
  them, so the package would ship with no `providers/` and the warning at `:114-115`
  would not even fire. Relax the condition (fire when `PackAsTool` is true, or always on
  publish) — providers already build without a RID (`:103`), so the RID-less case is
  the easy one.
- `nb.csproj:3-11` — `PackAsTool`, `ToolCommandName=nb`, `PackageId`, `Version`
  (none set today; `GenerateAssemblyInfo=false` at `:8`). Plain framework-dependent
  packaging: .NET 10's RID-specific tool packages write a settings format older SDKs
  refuse. Do **not** add `PublishTrimmed`/`PublishSingleFile` (none present now):
  `AssemblyLoadContext` loading (`ProviderManager.cs:43`) is a documented trimming
  incompatibility, and `McpManager.cs:349` still resolves via
  `Assembly.GetExecutingAssembly().Location`, empty under single-file (should move to
  `AppContext.BaseDirectory` like `ConfigurationService.cs:66`, `NbHarness.cs:146`).
- `nb.csproj:37-44` — `prompts/*.md` are already output items, so they ride into the
  package; `appsettings.example.json` is `Never` (`:24-25`), as `docs/distribution.md`
  intends — a tool install has no config until `--config` or `~/.config/nb`.
- Package id: check whether bare `nb` is free on nuget.org before choosing; if not,
  `Dreamlands.Nb` with `ToolCommandName` still `nb` (proctor's
  `learnings/name-collisions.md`: IDs are flat, the command name is per-package metadata).
- `docs/distribution.md` — a "Packing as a tool" section beside the publish one.

## Test

A script under `evals/` (or a CI step): `dotnet pack nb.csproj -c Release -o "$tmp/pkg"`,
`dotnet tool install nb --tool-path "$tmp/tp" --add-source "$tmp/pkg"`, then the command
above with `--config evals/test-appsettings.json`; assert exit 0 and a last line of
`type == "result"`, `exit_reason == "ok"`. A package missing `providers/` fails this
loudly: no Mock client can be built and the run exits 1
(`ProgramEvaluator.cs:384-396`). Also assert `ls "$tmp/tp/.store/**/providers"` lists
all seven directories the dev loop has (`bin/Debug/net10.0/providers/`).

## Open decisions

- Whether the publish-time warning at `nb.csproj:114-115` becomes an error for the pack
  path: a package with no providers is worse than a failed pack.
- Where `<Version>` lives (`nb.csproj`, or a `Directory.Build.props` shared with
  `nb.Core` so the trailer's `nb_version` and the package agree) — decided with the
  release workflow item.
