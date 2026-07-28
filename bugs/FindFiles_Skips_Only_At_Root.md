# find_files only skips bin/obj/node_modules at the repo root

Status: Fixed (2026-07-28) — found while investigating
`bugs/nb_State_Files_Leak_Into_Discovery.md`. Higher impact than that one.

## Fix

`FindFilesTool.cs` now adds both forms of each exclusion — `{dir}/**` for the
root-anchored case and `**/{dir}/**` for any depth. Regression coverage in
`nb.Tests/FindFilesToolTests.cs`:

- `FindFiles_SkipsNestedBinObjNodeModules` — the fixture from this report,
  verbatim; expects only `real.txt` and `proj/real.txt`.
- `FindFiles_SkipsDeeplyNestedSkipDirectory` — three levels down.
- `FindFiles_SkipDirectoryAsSearchRoot_StillSearchable` — asking for `bin/`
  explicitly still returns its contents. Exclusions are relative to the search
  root, so the skip list filters incidental hits, not deliberate ones.

Both regression tests were confirmed to fail against the unfixed tool.

The pre-existing skip tests (`FindFiles_SkipsGitDirectory`,
`FindFiles_SkipsNodeModules`, `FindFiles_SkipsBinObj`) all placed their junk at
the search root, which is why the suite was green while the bug was live.

---

## Symptom

`find_files` excludes `bin`, `obj`, `node_modules`, `.git` etc. **only when they
sit directly in the search root.** The same directories one level down are
enumerated in full.

Controlled fixture — identical junk at root and under `proj/`:

```
./bin/x/f.txt            ./proj/bin/x/f.txt
./obj/x/f.txt            ./proj/obj/x/f.txt
./node_modules/x/f.txt   ./proj/node_modules/x/f.txt
./real.txt               ./proj/real.txt
```

`find_files **/*` returned:

```
.nb_conversation_history.lock
proj/bin/x/f.txt            <-- should have been skipped
proj/node_modules/x/f.txt   <-- should have been skipped
proj/obj/x/f.txt            <-- should have been skipped
proj/real.txt
real.txt
```

Root-level `bin/`, `obj/`, `node_modules/` were correctly excluded. Their nested
twins were not.

## Cause

`Shell/FindFilesTool.cs:67-69`:

```csharp
foreach (var dir in SkipDirectories)
{
    matcher.AddExclude($"{dir}/**");
}
```

In `Microsoft.Extensions.FileSystemGlobbing`, a pattern whose first segment is a
literal name is **anchored at the search root**. `bin/**` therefore means
"`bin` directly under root", not "any directory named `bin`". Matching at any
depth requires a leading globstar: `**/bin/**`.

`GrepTool` does not have this bug — it walks manually and tests each directory
name as it descends (`GrepTool.cs:237`), which is depth-independent. `ListDirTool`
is single-level so the question doesn't arise. The defect is specific to the
glob-based exclusion in `FindFilesTool`.

## Why this matters more than it looks

**This repo is exactly the worst case.** nb is a multi-project .NET solution, so
`bin/` and `obj/` exist under `nb.Tests/`, `mcp-servers/mcp-tester/`, and every
one of `Providers/*/`. Only the root `bin/` and `obj/` are being skipped, so a
`find_files **/*` here enumerates build output from every other project —
hundreds of `.dll`, `.pdb`, `.json` and `.cache` files.

The user-visible cost is context: the model asks for the file list, receives a
wall of build artefacts, and burns budget on them. The tool's own description
advertises `Automatically skips: .git, node_modules, bin, obj, …`
(`FindFilesTool.cs:41`), so the model has no reason to doubt the result — it
believes it is seeing a filtered view.

## Repro

```bash
mkdir -p /tmp/t/{bin,obj,node_modules}/x /tmp/t/proj/{bin,obj,node_modules}/x
for d in bin obj node_modules proj/bin proj/obj proj/node_modules; do
  echo junk > /tmp/t/$d/x/f.txt
done
echo real > /tmp/t/real.txt; echo real > /tmp/t/proj/real.txt
cd /tmp/t && nb --trust "call find_files with pattern **/* and list every path returned"
```

Expected: 2 files. Actual: 5 (+ nb's lock file).

## Fix

Change the exclude to match at any depth:

```csharp
matcher.AddExclude($"**/{dir}/**");
```

Worth also excluding the directory entry itself (`**/{dir}`) so an empty skipped
directory can't surface, and adding a test — `nb.Tests/FindFilesToolTests.cs`
exists but evidently has no nested-skip case, which is how this survived.

Verify the fix against the fixture above; a root-only test cannot distinguish
the broken behaviour from the fixed one.

---

## Predates the conversation-program merge (2026-07-28)

Written against the pre-`nb.Core` architecture — interactive REPL, per-directory
conversation history, kits — which the merge in `92da725` replaced. Paths and
symbols named above may have moved: `Shell/FindFilesTool.cs` is now
`nb.Core/Shell/FindFilesTool.cs`.

The fix itself carried through the merge and its tests still pass. Nothing else
here was re-audited against the new architecture. Do that when closing.
