# nb's own state files show up as project files

Status: Fixed (2026-07-28) — narrow fix (candidate 1). Candidate 2 was
considered and deliberately not taken; see below.

## Fix

New `Shell/NbStateFiles.cs` owns both the canonical filenames and the ignore
set. `find_files` and `list_dir` filter against `NbStateFiles.All`; `Program.cs`
and `ConversationManager.cs` now build their paths from the same constants, so
renaming a state file cannot leave the filter matching a name nothing writes.

Two things surfaced while fixing it that the report didn't anticipate:

- **`FindFilesTool` held a private duplicate of the skip-directory list** rather
  than using `DefaultSkipDirectories.All`. It happened to agree, but this is
  precisely the drift the report warned about, one layer up. Collapsed onto the
  shared list.
- **File exclusions need the same both-forms treatment as directories.** A bare
  filename is anchored at the search root in `Microsoft.Extensions.FileSystemGlobbing`,
  so `.nb_conversation_history.json` and `**/.nb_conversation_history.json` are
  both required — the same anchoring rule as
  `bugs/FindFiles_Skips_Only_At_Root.md`.

Regression coverage, all four confirmed failing against the unfixed tools:

- `FindFiles_SkipsNbStateFiles` — this report's fixture verbatim
- `FindFiles_SkipsNbStateFilesWhenNested` — state files below the search root
- `ListDir_SkipsNbStateFiles`
- `ListDir_DirectoryHoldingOnlyStateFiles_ReportsEmpty`

`nb.Tests/ListDirToolTests.cs` is new — `list_dir` had no tests at all, so its
directory-skipping and path handling are now covered too.

Verified end-to-end against a local model with the report's own repro: in a
directory holding all three state files, `find_files **/*` and `list_dir` each
returned only `notes.txt` and `README.md`. That run is the only way to exercise
the `.lock` case, since the lock exists solely while nb is running.

**Candidate 2 (opt-in for dotfiles) not taken.** It remains the fix that stops
the *class*, but it is a behaviour change that would also hide `.github/`,
`.gitignore` and similar, which are legitimately interesting. Worth revisiting
on its own terms rather than riding along with this.

Still true: the history-file half goes away entirely if the
`.nb_conversation_history` machinery is removed per `TODO.md` and
`plans/composable-cli-reorientation.md`. `.nb_active_kits.json` would remain,
so the list keeps earning its keep either way.

---

## Symptom

`find_files` and `list_dir` return nb's own per-directory state files as if they
were part of the user's project:

```
• find_files: **/*
  → .nb_conversation_history.json
    .nb_conversation_history.lock
    notes.txt
    README.md
    ...
```

The model then dutifully lists them. Asked "find every file in this directory
tree", it reported `.nb_conversation_history.lock` as the first item of the
user's project.

`.nb_conversation_history.lock` is the more annoying half: it exists only
*while nb is running*, so the tool is showing the model an artifact of the
observation itself. It is never present when the user looks at the directory
afterwards, which makes it confusing to explain.

## Cause

Exclusion is directory-only. `Shell/DefaultSkipDirectories.cs` holds a set of
directory *names* (`.git`, `node_modules`, `bin`, `obj`, …) and there is no
corresponding file-level ignore list anywhere. nb's own state files are files in
the working directory, so nothing filters them.

Affected tools:

| tool | leaks? | why |
|---|---|---|
| `find_files` | **yes** | glob `**/*` matches dotfiles; no file exclusions |
| `list_dir` | **yes** | lists directory entries, filters directories only (`ListDirTool.cs:56`) |
| `grep` | no | content search — only surfaces files that match the pattern |

## Repro

1. `cd` to any directory and run `./nb --trust "find every file here"`.
2. `.nb_conversation_history.lock` appears in the results (and
   `.nb_conversation_history.json` too, once a previous run has saved one).

## Fix candidates

1. **Add a file-level ignore list** alongside `DefaultSkipDirectories` — at
   minimum nb's own artifacts (`.nb_conversation_history.json`,
   `.nb_conversation_history.lock`, `.nb_active_kits.json`). One shared list, so
   `find_files` and `list_dir` can't drift apart.
2. Consider whether dotfiles should be matched by a bare `**/*` at all. Most
   file-discovery tools require an explicit opt-in for hidden files; that would
   fix this class of problem rather than this instance of it. It is a behaviour
   change though — `.github/`, `.gitignore` etc. are legitimately interesting.

Option 1 is the narrow fix. Option 2 is the one that stops the next occurrence.

Note this becomes moot for the history files specifically if the
`.nb_conversation_history` machinery is removed — see `TODO.md` and
`plans/composable-cli-reorientation.md`. `.nb_active_kits.json` would remain.

Related: `bugs/FindFiles_Skips_Only_At_Root.md` — the same tool also fails to
exclude the directories it *does* know about, once they are nested.
