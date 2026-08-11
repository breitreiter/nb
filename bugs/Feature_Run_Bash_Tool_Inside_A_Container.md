# Feature: run the `bash` tool inside a running container

Status: Requested (2026-08-11) — wanted by a local harness that drives nb against
a containerized fixture, which currently works around it with PATH shims.

## What is wanted

A way to say *"execute `bash` tool calls inside this container, not on this
host"*, while nb itself keeps running on the host.

The natural home is the existing sandbox axis, which already has this shape
(§5.3 — `approval sandbox none|bwrap|bwrap-net`):

```
approval sandbox container
approval container podman exec -w /work trial-app
```

…or a single directive, if that reads better:

```
bash exec podman exec -w /work trial-app
```

Either way the contract is: nb builds the command as it does now and hands it to
the configured launcher instead of the host shell. Working directory, exit code
and stdout/stderr pass through unchanged.

## Why the existing options do not cover it

The harness runs an agent against a fixture repo and grades what it produces. Two
requirements collide:

- **The agent must reach the network** — the model is served over HTTP
  (llama.cpp on `127.0.0.1:8081`), so nb cannot run without an interface.
- **The workspace must have none** — trials run in a `--network=none` pod so a
  build cannot silently fetch from the real module registry, which is what makes
  results comparable between runs weeks apart.

`bwrap` does not resolve this: it sandboxes the filesystem and removes the
network for the *bash child*, but the toolchain still has to exist on the host,
and it does not. The build environment here is a container image — Go plus a
pre-warmed module cache — and reproducing it on the host defeats the point.

## The current workaround, and what it costs

A shim on `PATH` that forwards one binary into the container:

```bash
#!/usr/bin/env bash
exec podman exec -w /work trial-app go "$@"
```

It works, but it leaks a second filesystem namespace into the agent's view, and
that is a real confound for anything measuring agent behaviour:

- The agent sees **host** paths from `list_dir` and `read_file`
  (`/tmp/…/trial1/work/main.go`) but the shim always executes at the
  **container** path (`/work`). Same files, two names.
- `cd` becomes a no-op with respect to where builds actually run. In the first
  trial the model tried `cd /work && go mod tidy`, then `cd work && go mod tidy`
  — sensible in both namespaces, wrong in this hybrid.
- Every binary the agent might reasonably reach for needs its own shim, so the
  environment is quietly non-standard in ways the agent cannot discover.

With bash redirected wholesale, none of that arises: one filesystem, one set of
paths, `cd` behaves, and any tool present in the image is available.

## Notes

- Docker and podman both accept `exec -w <dir> <container> <cmd…>`, so a
  configurable launcher prefix covers both without nb knowing which is in use.
- Worth deciding what `BashTimeoutSeconds` means when the child is remote — the
  timeout should presumably still be enforced by nb, with the container process
  killed on expiry, or a hung build outlives the run.
- This composes with `approval bash` rather than replacing it: the allow-list
  still decides *what* runs, this decides *where*.
