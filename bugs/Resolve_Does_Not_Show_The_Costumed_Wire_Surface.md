# `--resolve` reports canonical tool names, so a costume's real surface is invisible

Status: Open (2026-08-16) — found while preparing harness arms for a comparison
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
