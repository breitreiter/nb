---
kind: bug
title: 'Feature: a release workflow that publishes the tool package to nuget.org'
created: 2026-09-17
updated: 2026-09-17
status: current
state: open
severity: low
cluster: distribution
---

# Feature: a release workflow that publishes the tool package to nuget.org

Status: Open (2026-09-17) — filed from proctor (`proctor/project/todo.md`, "Candidates
for nb, not proctor"; `learnings/ci-distribution.md`, "Where nb is today", "What this
adds to the design"). Against `e34e762`.

## What is wanted

A `.github/workflows/release.yml` that, on a version tag (`v*`):

1. packs the tool package (`Feature_PackAsTool_Carries_Providers_Into_The_Tool_Package.md`)
   and pushes it to nuget.org — trusted publishing (OIDC via `NuGet/login`) preferred,
   an API-key secret as the fallback;
2. publishes the self-contained per-RID builds `ci.yml:34-38` already makes, archives
   them (`nb-<version>-<rid>.tar.gz` / `.zip`), and attaches them to a GitHub Release on
   the tag — the install path for runners without a .NET SDK.

## Why

nb cuts no releases today. `ci.yml:40-44` uploads the per-RID publishes as a workflow
artifact, which expires; there is no tag trigger (`ci.yml:3-7` is push/PR on master), no
release step, no package. The README already points users at the releases page
(`README.md:96`) and two tags exist (`v0-stable`, `v0.9-beta`), so the intent is there
and nothing fills it. proctor's consumer story — a tool manifest pinned to an nb version
that Dependabot bumps — needs a package that exists.

## Additive guarantee

Tag-triggered only; pushes and pull requests run exactly the jobs they run now. Nothing
in the build, tests or evals changes. A consumer building from source sees no difference.

## Where it lands

- New `release.yml` with `on: push: tags: ['v*']`, `permissions: id-token: write,
  contents: write`. Steps: checkout, setup-dotnet 10.0.x (`ci.yml:16-19`), pack, push,
  the RID loop from `ci.yml:36-38` (publishing `nb.csproj` alone — the MSB3030 race in
  the comment at `ci.yml:30-33` still applies), archive, `gh release create`.
- **Version source.** No `<Version>` exists in `nb.csproj` or `nb.Core.csproj`, and both
  set `GenerateAssemblyInfo=false` (`nb.csproj:8`, `nb.Core.csproj:7`). Two honest
  options: derive from the tag (`-p:Version=${GITHUB_REF_NAME#v}` on pack and publish),
  or put `<Version>` in a `Directory.Build.props` and have the workflow fail when the tag
  disagrees. The tag-derived form cannot drift; the props form makes `nb_version` on the
  trailer correct for local builds too. Prefer props plus the equality check.
- **No third build+test copy.** `ci.yml:24-28` and `test.yml:11-13` both build and test
  today (test.yml also runs `evals/run.sh --skip-llm`, which `ci.yml` does not — the gap
  CLAUDE.md:55-60 warns about). The release workflow should not add a third: either run
  it `on: workflow_run` after CI succeeds on the tagged commit, or make it pack-and-
  publish only and rely on the tag being cut from a green master. Folding `test.yml`
  into `ci.yml` is a separate cleanup this item should not smuggle in.
- `docs/distribution.md` — how a release is cut (tag, what runs, where it lands).
  `README.md:79-100` — an "Install as a .NET tool" section above the binaries.

## Test

Dry-run on a pre-release tag (`v0.9.0-rc.1`): the workflow pushes to nuget.org, and
`dotnet tool install nb --version 0.9.0-rc.1 --tool-path /tmp/tp` followed by the Mock
smoke line from the pack item exits 0 on a clean machine. `gh release view v0.9.0-rc.1`
lists three archives. A second push of the same version must fail at nuget.org
(immutable), which is the guard against re-tagging.

## Open decisions

- Trusted publishing needs the package to exist once already (first push is by key);
  say so in `distribution.md` rather than discovering it on release day.
- Whether `evals/run.sh --skip-llm` gates the release; today it gates only `test.yml`.
- Signing is out of scope (`docs/distribution.md:26-27`); the release notes should
  keep saying so.
