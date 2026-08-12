---
kind: plan
title: Run the bash tool inside a container
created: 2026-08-11
updated: 2026-08-11
status: current
state: proposed
touches:
  files:
    - Shell/BashTool.cs
    - Shell/ApprovalPolicy.cs
    - Shell/ShellEnvironment.cs
    - Shell/ContainerExec.cs
    - ProgramEvaluator.cs
    - Transcript/ProgramParser.cs
  features: [approval, sandbox, bash, headless]
provenance:
  author: claude
  source: bugs/Feature_Run_Bash_Tool_Inside_A_Container.md
---

# Run the bash tool inside a container

## Why this plan exists

`bugs/Feature_Run_Bash_Tool_Inside_A_Container.md`. A harness drives nb against a
containerized fixture and grades what the agent produces. It needs two things that
cannot both hold on the host:

- nb must reach the network (the model is served over HTTP on `127.0.0.1:8081`);
- the workspace must not (a `--network=none` pod, so a build cannot silently fetch
  from the real module registry and results stay comparable across weeks).

`bwrap` does not solve it. It removes the network from the bash child and confines the
filesystem, but the toolchain still has to exist *on the host*, and here it does not —
the build environment is a container image (Go plus a pre-warmed module cache).

The current workaround is a PATH shim per binary (`exec podman exec -w /work trial-app
go "$@"`). It works and it poisons the measurement: the agent reads host paths from
`list_dir`/`read_file` but every shimmed command executes at a container path, so `cd`
is a no-op with respect to where builds run and the environment is non-standard in
ways the agent cannot discover.

## The shape

A third sandbox mode. `SandboxMode` is already the axis that decides how the bash child
is contained, and `BashTool.ConfigureCommand` is already the single place that turns a
command string into a `ProcessStartInfo`:

```
approval sandbox container
approval container podman exec -w /work trial-app
```

nb builds the command exactly as it does now and hands it to the configured launcher
instead of the host shell. Working directory, exit code, stdout/stderr, truncation and
`tool_error_limit` accounting all pass through unchanged.

Two directives rather than one because the mode and the launcher are separately useful:
`sandbox container` is what the policy reasons about, `container <argv…>` is the
deployment detail. Docker and podman both accept `exec -w <dir> <container> <cmd…>`, so
a launcher prefix covers both without nb knowing which is in use.

## The constraint that makes v1 tractable

**The workspace must be bind-mounted at the same path on both sides, and nb does not
translate paths.**

This is the crux. `read_file`, `write_file`, `edit_file`, `list_dir`, `find_files` and
`grep` are in-process .NET — they never route through bash and so never enter the
container. If the host sees `/tmp/trial1/work` and the container sees `/work`, the
"same files, two names" confound the bug report wants removed *survives the feature*,
just relocated from the shim to nb itself.

Path translation (rewriting tool arguments and tool output across a mount table) is a
much larger feature and a leaky one — it would have to rewrite paths inside command
*output* too, which is unbounded. So v1 declares the constraint instead: run the
container with `-v /tmp/trial1/work:/tmp/trial1/work -w /tmp/trial1/work`, and the two
namespaces agree by construction. Anything else is out of scope and should fail loudly
rather than half-work.

Consequence for the harness: the fixture image needs the workspace mounted at its host
path, which is a one-line change to how the pod is started, and cheaper than every
alternative that keeps the paths different.

## Work

1. **`SandboxMode.Container` + launcher state.** Add the enum member; `ApprovalPolicy`
   grows a `ContainerLauncher` (string argv) alongside `Sandbox`/`SandboxNet`, set by
   the same directive path. `BwrapSandbox.TryParse` gains `container`, or the parse
   moves to a small `SandboxSpec.TryParse` covering all modes — the current name is
   already wrong for a third mode.

2. **`ContainerExec.BuildArgs(launcher, shellPath, command, cwd)`.** Mirrors
   `BwrapSandbox.BuildArgs`: splits the launcher prefix into argv, appends
   `bash -c <command>` as *separate* argv entries (`ArgumentList`, no shell escaping —
   the bwrap arm already establishes this pattern and it is the correct one). Where the
   launcher already carries `-w`, nb does not add another; see (4).

3. **`ConfigureCommand` arm.** ~15 lines, structurally identical to the bwrap arm.
   `psi.WorkingDirectory` stays the host cwd (it is where `podman` itself runs, which is
   harmless); the *effective* cwd comes from the launcher's `-w`.

4. **cwd and `set_cwd`.** Decide and document: the launcher's `-w` is authoritative, and
   `set_cwd` appends `-w <newcwd>` to the launcher argv for subsequent calls (a later
   `-w` wins for both docker and podman, so this needs no argv surgery). Under the
   same-path constraint this stays coherent — `set_cwd` names one path that means the
   same thing on both sides. Validate at directive time that the launcher's `-w`, if
   present, matches the host cwd, and warn otherwise; that check is what turns a
   misconfigured mount into a startup warning instead of a silently hybrid run.

5. **Timeout.** The real wrinkle. Today a timeout does `process.Kill(entireProcessTree:
   true)`, which kills the `podman exec` *client* — the process inside the container
   keeps running, and a hung build outlives the run exactly as the bug report predicts.
   nb must still own the clock, so on expiry: kill the client as now, then issue a best
   effort `<launcher-runtime> exec <container> pkill -TERM -f <marker>` or, simpler and
   more reliable, launch the child with a marker env var and kill by that. Needs a
   deliberate decision; a plausible v1 is to kill the client, report the timeout
   honestly, and *say in the tool result* that the container-side process may still be
   running rather than pretending it was reaped.

6. **`ShellEnvironment`.** Detection (`OS`, `ShellPath`, `AvailableTools`,
   `MissingTools`, `CaseSensitiveFs`) probes the host and
   `BuildSystemPromptSection()` injects it into the system prompt. Under container-exec
   that describes the wrong machine — it tells the agent `go` is missing when `go` is
   exactly what is available. Options, cheapest first:
   - **(a)** Probe through the launcher: run the same `which`-style detection via
     `ContainerExec` at startup. Costs one container round-trip per probed tool (batch
     them into a single `bash -c`), and is the only option that yields a *true* answer.
   - **(b)** Suppress the tool inventory under container mode and state in the prompt
     that bash executes inside a container image, so the model discovers by trying.
   Prefer (a) with (b) as fallback when the probe fails. This is the largest single
   piece of the work and the one that most determines whether the feature actually
   removes the confound.

7. **Startup validation.** Requesting `sandbox container` with no launcher, an
   unreachable runtime, or a container that is not running should hard-fail the run
   (exit 1) the way `bwrap`-when-unavailable does — same code path in
   `ProgramEvaluator`. A one-shot `<launcher> true` at directive time is a cheap probe.

8. **Docs + tests.** §5.3 gains the mode and the `container` key, plus the same-path
   constraint stated as a requirement rather than a footnote. Tests: argv construction
   (unit, no container needed), parse/validation of both directives, and the
   hard-fail-when-unavailable path. Anything needing a live container stays out of the
   default suite.

## Explicitly not in v1

- **Path translation / mount tables.** See the constraint above.
- **nb starting or tearing down containers.** The launcher targets an already-running
  container; lifecycle belongs to the harness. `podman run --rm` as a launcher works by
  accident (fresh container per call, no state between them) — document that it is not
  the supported shape.
- **Routing file tools through the container.** Same-path mount makes it unnecessary;
  doing it properly means an execution backend behind every file tool, which is a
  different plan.

## Notes

- This composes with `approval bash` rather than replacing it: the allow-list still
  decides *what* runs, the sandbox mode decides *where*. Note the interaction with the
  whole-command-string matching documented in §5.3 — a container run under
  `approval default deny` needs its allow patterns written against the full command
  line, which is exactly the friction the original bug report hit.
- `BashTimeoutSeconds` semantics change enough under a remote child that the config doc
  should say so explicitly.
