---
kind: bug
title: '`--resolve` reports canonical tool names, so a costume''s real surface is invisible'
created: 2026-08-16
updated: 2026-09-05
status: current
state: fixed
severity: medium
cluster: schema-vs-dispatch
---

# `--resolve` reports canonical tool names, so a costume's real surface is invisible

Status: **Fixed 2026-09-05** — the suggested fix, output as sketched. Originally: Open (2026-08-16) — found while preparing harness arms for a comparison
that drives nb headlessly against containerized fixtures. Measured against
`ed2bdd7`, release build.

## Symptom

`--resolve` prints the same `tools=` list whatever harness the program wears, so
it cannot be used to check what the model will actually be offered.

The program is unchanged apart from a prepended `harness` directive:

```
tools none
tools +read_file +write_file +edit_file +list_dir +find_files +grep +bash +search_web +fetch_url
```

```
$ nb --config nb-config.json --resolve rails-happy.nb
run 1: … harness=nb     … tools=bash,edit_file,fetch_url,find_files,grep,list_dir,read_file,search_web,write_file …

$ nb --config nb-config.json --resolve rails-codex.nb
run 1: … harness=codex  … tools=bash,edit_file,fetch_url,find_files,grep,list_dir,read_file,search_web,write_file …
```

Nine tools reported. Under `harness codex` the model is offered **two**:
`shell_command` (from `bash`) and `view_image` (from `read_file`). `apply_patch`
is not in the surface, and `update_plan` needs `todo`, which this program
excludes — so `CodexHarness.CreateTools` declares neither.

**The run has no way to edit a file, and `--resolve` says it has `edit_file`.**

## Why the output is not wrong, exactly

It is the documented vocabulary. The README is explicit and right:

> Those names are **canonical under every harness**. A costume changes the names
> the *model* sees, not the names a program writes.

So `tools=` is faithfully echoing the surface *directives*. The gap is that
`--resolve` is sold as the pre-flight instrument —

> `nb --resolve flow.nb` — print the effective envelope … at each run point

— and under a costume the tool half of that envelope is not effective, it is
requested. The costume is precisely the thing that makes requested and effective
diverge, because a costume declares a *subset* under different names: codex has no
file-read or grep tool at all, and everything a canonical name maps to may simply
vanish.

This is the same class of failure the harness registry already refuses to allow
elsewhere — an unknown `harness` name is a hard error rather than a fallback,
because "comparative numbers that mean nothing" is worse than a missing run. A
silently two-tool codex run produces exactly those numbers.

## Suggested fix

Print the wire surface alongside the canonical one whenever a costume is worn:

```
run 1: … harness=codex tools=bash,edit_file,…,write_file
       wire=shell_command,view_image  dropped=edit_file,write_file,find_files,grep,list_dir,search_web,fetch_url
```

The `dropped` half is the load-bearing part — a costume that quietly discards
seven of nine named tools is a condition worth seeing before spending a run, and
it is derivable from the same `CreateTools(surface)` call the run will make.

Worth considering alongside it: a **warning when a costume drops a tool the program
explicitly named with `+`**. A `+` is an assertion that the run needs that tool.
Dropping it is not the same as it never having been asked for, and `--validate`
already treats a costume's wire name in a `tools` directive as an error rather than
a no-op — for the same reason, in the other direction.

`nb.Tests/ToolSurfaceGoldenTests.cs` already computes exactly this surface for the
golden files, so the machinery exists; it just is not reachable from the CLI.

## Workaround

None from nb. A caller has to hardcode each costume's gating rules on its own side
to know what a program will really get, which will drift from the costume classes.

---

## Fix (2026-09-05)

`--resolve` prints a second line whenever a costume is worn, in the shape this report
sketched:

```console
$ nb --resolve rails-codex.nb
run 1: … harness=codex … tools=bash,edit_file,fetch_url,find_files,grep,list_dir,read_file,search_web,write_file …
       wire=shell_command,view_image dropped=edit_file,fetch_url,find_files,grep,list_dir,search_web,write_file
```

The canonical `tools=` line is untouched, because the README is right that those names
are the program's vocabulary. The wire line is additive and appears **only** under a
costume — with `harness nb` there is no divergence to report and the line would be noise
on every run.

**How `dropped` is derived, since it is not obvious.** Costumes keep no canonical-to-wire
map — `CreateTools` is a run of independent `if (X != null && surface.AllowsNative("x"))`
arms — so there is nothing to read off. Instead the resolver probes the costume one
canonical tool at a time: a name is dropped when `CreateTools` yields nothing for a
surface containing only that name. That is exact rather than approximate here precisely
because each arm gates on a single `AllowsNative` check, so a tool that yields nothing
alone yields nothing in company. It also means the report cannot drift from the costume
classes, which is what the workaround section says a caller was otherwise forced into.

The probe builds every capability, including `search_web` (normally config-gated on an
API key), so a name missing from the wire surface is always the costume's decision and
never an absent tool.

Verified across all three costumes: codex drops seven of nine as reported; claude-code
drops nothing, because it advertises the whole surface under its own names.

### Not done: the warning on a dropped `+`

The report's adjacent suggestion — warn when a costume drops a tool the program named
explicitly with `+` — is **not** implemented, and the reason is worth recording rather
than leaving as an omission. `ToolSurface.Fold` collapses the directives into a single
`NativeAllow` set, so by the time a run holds a surface, "named with `+`" and "left in by
default" are indistinguishable. Warning on every dropped tool instead would fire on
entirely reasonable programs (`tools none +bash` under codex drops nothing the author
wanted), which is how a warning becomes noise. Implementing it properly means carrying
the explicit-`+` names alongside the folded surface; that is a small change to
`ToolSurface`, not a free addition, and it is queued rather than smuggled in here.

### Tests

Four evals in `evals/run.sh` (the built binary, asserting on stdout — which is where
`--resolve` lives; there is no unit-test surface for it). They pin the wire list, the
dropped list, that the canonical line is unchanged, and that a native-harness run prints
no wire line at all. The last one needed a new `run_prog_stdout_lacks` helper.
