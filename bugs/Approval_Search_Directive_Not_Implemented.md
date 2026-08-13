# `approval search` is documented but the program parser rejects it

Status: Fixed (2026-08-12).

## Fix

`search` added to the accepted keys in `ValidateProgram` (`Program.cs`), plus a
value check (`allow | prompt`) matching how `default` and `sandbox` are already
validated — an unknown value would otherwise pass `--validate` and then warn at
run time, which is the wrong way round for a flag whose whole point is to be
checked before a headless run.

Two stale key lists in `ProgramParser` (the `ParseApproval` comment and its
missing-value error message) also omitted `search`; both updated.

The adjacent §5.2 item was real: `search_web` was missing from the native tool
list in the docs. Added — the list now matches
`ConversationManager.NativeToolNames` exactly, which is where it should have been
checked against in the first place. Nothing enforces that agreement; a doc/code
sync test would be the real fix if it drifts again.

## One correction to the report

The report says *"the config route works and the program route does not"*. The
program route does work — the directive was fully wired in the evaluator
(`ProgramEvaluator.cs:139`, `search` → `SetSearchAllowed`), and a program
carrying `approval search allow` runs correctly today. The parser accepted it too
(key validity is semantic there, by design).

`--validate` was the only thing rejecting it. That is still the bug — §5.3 points
harness authors at `--validate`, so a correct program failed the check it was
told to run — but the blast radius was narrower than "the feature is
unimplemented": nobody's run was silently mis-approved, they just could not
validate. Verified before and after:

```console
$ ./nb --validate trial.nb    # before: error: invalid approval key 'search'. exit=1
$ ./nb --validate trial.nb    # after:  valid: 3 directive(s). exit=0
```

Regression test in `nb.Tests/ProgramParserTests.cs` (`Approval_Search_Parses`)
pins the parser half. `ValidateProgram` is private to the CLI Exe and reads
static flag state, so the validator half is verified manually — as with
`bugs/Bad_Program_Path_Crashes_With_Stack_Trace.md`. Two CLI-shell bugs in a row
have now been untestable for the same reason; that is the argument for the
integration harness, not for opening these up one at a time.

---

Status: Open (2026-08-12) — found wiring `search_web` into a headless harness,
against `61b5a65`.

## Symptom

```
$ cat trial.nb
approval default deny
approval search allow

$ nb --validate trial.nb
error: invalid approval key 'search'. Valid: bash, mcp, default, sandbox.
invalid: 1 error(s).
```

## Mechanism

The feature is implemented everywhere except the directive validator.

- `docs/conversation-program-cli.md` §5.3 documents the key, and explains why it
  matters: *"Needed for headless runs … without this every headless run reads as
  a denial."*
- `nb.Core/Facade/NbRuntime.cs:144` reads `Approval:Search` from config, and its
  comment names both routes — *"Approval.Search in config, or the `approval
  search allow` directive."*
- `nb.Core/Shell/ApprovalPolicy.cs:32` carries `_searchAllowed`.
- `Program.cs:418` allows only `bash`, `mcp`, `default`, `sandbox`.

So the config route works and the program route does not.

## Why it matters more than a missing key usually would

`search_web` exists to make search *intent* observable in the transcript. The
documented failure mode of not approving it is that a headless run records a
denial instead of the intent — which is precisely the signal the tool was added
to capture. A harness author following §5.3 hits a hard validation error; one who
does not notice the config alternative silently measures denials.

It also splits the interface: everything else about a run is expressible in the
program, which the docs describe as "the whole design: the program is the
interface". Approval for search is currently the one exception.

## Workaround

Set it in the config file instead:

```json
{ "Approval": { "Search": true } }
```

## Fix

Add `search` to the accepted keys in `Program.cs:418` and map it to the same
`_searchAllowed` path the config route uses.

## The same gap, one tool over: `fetch_url` has no approval path at all

Found 2026-08-13, wiring the same harness. `DecideFetch()` (`ApprovalPolicy.cs`)
is a one-liner that always returns `NonMatch` — there is no glob, no config key,
no `--approve` form. The doc comment says *"never auto-approves in v1"*, so this
is deliberate, but the consequence is the one §5.3 warns about for search:

**`fetch_url` cannot execute in any headless run.** Under `approval default deny`
it denies; under `prompt` with no TTY it denies. A program may declare the tool,
the model may call it, and the call can never succeed.

That is defensible as a safety default — fetching arbitrary URLs is a bigger
capability than searching — but it should be *reachable*, for the same reason
search is: nb's primary mode is non-interactive. The natural shape is a glob, so
a harness can allow a host or prefix rather than the whole web:

```
approval fetch https://docs.example.com/*
```

which also reads better than search's all-or-nothing `allow`. Worth deciding
alongside the search key rather than after it, since a caller reasonably expects
the two network tools to be configured the same way.

## Adjacent, while you are in there

`docs/conversation-program-cli.md` §5.2 lists the native tool names as
`bash, read_file, write_file, edit_file, find_files, grep, list_dir,
apply_patch, fetch_url, todo` — **`search_web` is missing**, though
`ConversationManager.cs:102` includes it. Someone reading only §5.2 would not
know the tool exists.
