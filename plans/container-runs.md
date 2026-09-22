---
kind: plan
title: Running nb in a container, well — --compile, a Containerfile, the runbook
created: 2026-09-21
updated: 2026-09-21
status: in progress (items 1 and 2 of 3 done; 3 waits on proctor's live run)
state: active
touches:
  files:
    - Program.cs
    - Containerfile
    - docs/containers.md
    - docs/distribution.md
  features: [headless, docs, distribution, oracle]
provenance:
  author: claude
  source: plans/harness-cookbook.md
  note: proposed 2026-09-21 with proctor's plan for containerised runs (../proctor/project/plans/containerised-runs.md); the three items are the cookbook's unbuilt "ship a program over stdin" and "the basic container case" rows
---

# Running nb in a container, well

## Why this plan exists

The standard deployment is one container, nb inside it, one filesystem
(`CLAUDE.md`, *nb does not confine the tools it runs*). Every harness that
does it derives the invocation again, and `plans/harness-cookbook.md` lists
the pieces that would stop that: a program that can travel over stdin with
its includes already resolved, and a runbook nobody has written. Proctor is
the first consumer to build its side against a contract rather than a
derivation, so this is the moment to ship nb's side. Proctor's plan says
what it will hand a runner and what it expects back; nothing there needs
nb's CLI or wire format to change beyond the one flag below.

## Three items

### 1. `--compile`

Parse the program, resolve `@file` includes, emit the JSONL bytecode, run
nothing. The size of `--resolve`, and the same code path up to evaluation.

```bash
nb --compile flow.nb > flow.jsonl          # on the host, beside the eval
podman run -i … /opt/nb/nb --output jsonl - < flow.jsonl
```

The point is what is *not* in the container afterwards: the oracle sheet,
a costume's instruction file, a system prompt held in a separate file. The
transcript already carries the resolved sheet body rather than the path
(`plans/oracle-resolver.md`), so the compiled form is what the run would
have recorded anyway. `LooksLikeJsonl` already accepts the result on stdin.

Exit 1 on a parse error, like `--validate`. No config is needed to compile,
so it must work with no `appsettings.json` and no provider loaded.

*Status 2026-09-21: built.* Two things settled beyond the above: `--seed` folds
into the compiled output, so the container needs no seed file either; and the
directive-shape check (`approval` keys, `loop`/`budget` values) runs before
emission, so a program nb would refuse fails on the host rather than inside the
container. Nine eval lines under *compile* in `evals/run.sh`, including the
source-vs-compiled identical-run test and compile-of-JSONL as the identity.

### 2. A Containerfile

At the repo root, building the distribution publish into an image whose
only content beyond the base is `/opt/nb`: the self-contained linux-x64
binary, `providers/`, `appsettings.example.json`, and nothing that
`docs/distribution.md` says must not ship. `ENV
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`, so the layer works on a base
with no libicu (`bugs/Publishing_Nb_Into_A_Container_Is_Undocumented.md`
§4). Its intended use is as a layer, not as a runnable image:

```Dockerfile
FROM localhost/nb:latest AS nb
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=nb /opt/nb /opt/nb
```

Publishing it to a registry is a distribution question for later; the
Containerfile and a `podman build -t nb .` line in `docs/distribution.md`
are this plan's scope.

*Status 2026-09-21: built.* `Containerfile` at the root, `runtime-deps:10.0`
as the final base so the image runs on its own for a smoke test as well as
serving as a layer; `.dockerignore` is an allowlist so the developer's
`appsettings.json` and `mcp.json` never enter the build context. Building
it found that `dotnet publish nb.csproj` on a clean checkout had never
worked: nothing restored the provider projects (CI restores the solution
first, which hid it). The publish target now restores them itself. Verified
by hand with docker: `--version`, a Mock program with a mounted config, and
`--compile` with no config all run; the layer is 132 MB, 232 files, seven
providers, no `appsettings.json`, no `mcp.json`, no test platform.

### 3. `docs/containers.md`

The cookbook's *basic container case, and podman well* row, written once:

- image contents: nb, providers, the fixture, nothing else; where config
  enters (`--config` on a read-only mount, keys as `${VAR}` references and
  `-e`), and that the model shares the environment and can read the key;
- the invocation: `podman run -i`, program on stdin, transcript on stdout,
  chrome on stderr, exit code through; `-w` as the working directory the
  program names;
- rootless UID mapping and `--userns=keep-id`, so files the model writes
  are owned by the caller on the host;
- `--network none` versus a curated network, and that nb and the model
  share the namespace;
- the artefact inventory the cookbook asked for: what nb leaves in reach
  (binary, providers, config, seeds, the program if it is on disk) and the
  cheapest mitigation for each. This is where `--compile` earns its
  paragraph.

Not a security document; the *Sharp edges* framing from the cookbook
applies.

## Build order

1. `--compile`, with a test that compiles a program with an `@` include and
   an oracle sheet and runs the output through the Mock provider identically
   to the source.
2. The Containerfile and its line in `docs/distribution.md`.
3. `docs/containers.md`. Proctor's README links it rather than repeating it.

## Not in this plan

- `--config -` or an env-only config mode. Config on a read-only mount with
  keys from the environment covers the case; wait for the inventory to show
  otherwise, as the cookbook said.
- Any change to the bash tool or a container exec mode. That was rejected
  in `plans/container-bash-exec.md` and stays rejected.
