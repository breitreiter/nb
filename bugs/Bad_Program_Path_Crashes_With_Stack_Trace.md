# Bad program-file path crashes with an unhandled exception

Status: Fixed (2026-08-12) — the suggested pre-check, taken as written.

## Fix

`BuildProgramAsync` (`Program.cs`) checks `File.Exists` before reading the
positional program file and throws a message-carrying `FileNotFoundException`,
exactly as `--seed` does one block up. All three bad-path shapes now produce the
same one-line error and exit 1:

```console
$ ./nb /nonexistent/path/foo.nb   → Error: program file not found: /nonexistent/path/foo.nb
$ ./nb /tmp                       → Error: program file not found: /tmp
$ ./nb ./nope.nb                  → Error: program file not found: ./nope.nb
```

`--validate` inherits it (same path), and `-`/piped stdin is exempted from the
check. The catch filter at `Program.cs:336` was left alone: the pre-check makes
missing input *handled* rather than handled-by-accident-of-BCL-exception-choice,
which was the actual complaint.

The predicted message improvement landed too — the old graceful case said
`Could not find file '<abspath>'` with no hint which of the several file-taking
flags was at fault.

No regression test: `BuildProgramAsync` is private to the CLI Exe and reads
static flag state, so covering it would mean opening it up for the test alone.
Verified manually against all three shapes above.

---

Status: Confirmed (2026-08-12) against `bin/Debug/net10.0/nb` at master 61b5a65.

## What happens

Pointing nb at a program file whose *directory* does not exist crashes the
process instead of reporting a missing file:

```console
$ ./nb /nonexistent/path/foo.nb
Unhandled exception. System.IO.DirectoryNotFoundException: Could not find a part of the path '/nonexistent/path/foo.nb'.
   at Interop.ThrowExceptionForIoErrno(...)
   ...
   at nb.Program.BuildProgramAsync(IList`1 warnings) in /home/joseph/repos/nb/Program.cs:line 392
   at nb.Program.RunProgramAsync(IConfiguration config) in /home/joseph/repos/nb/Program.cs:line 334
   at nb.Program.Main(String[] args) in /home/joseph/repos/nb/Program.cs:line 203
Aborted (core dumped)
```

Exit code is **134** (SIGABRT / core dumped), not 1. For a CLI that scripts are
expected to drive, a core dump on a typo'd path is the wrong failure shape.

A second, related crash: passing a **directory** as the program file.

```console
$ ./nb /tmp
Unhandled exception. System.UnauthorizedAccessException: Access to the path '/tmp' is denied.
 ---> System.IO.IOException: Permission denied
```

## What is expected

The same shape the neighbouring paths already produce — a one-line error on
stderr and exit 1:

```console
$ ./nb ./nope.nb
Error: Could not find file '/home/joseph/repos/nb/bin/Debug/net10.0/nope.nb'
$ echo $?
1
```

## Why it splits this way

`Program.cs:336` catches only three exception types around `BuildProgramAsync`:

```csharp
catch (Exception ex) when (ex is TranscriptFormatException or ProgramParseException or FileNotFoundException)
```

`File.ReadAllTextAsync` (`Program.cs:394`) throws `FileNotFoundException` only
when the *file* is missing but its directory exists. A missing directory throws
`DirectoryNotFoundException`, and a directory-as-file throws
`UnauthorizedAccessException` — neither is a `FileNotFoundException`, so both
escape the filter and reach the runtime's unhandled-exception handler.

So the graceful case is graceful by accident of which sibling exception the BCL
happened to raise, not because the missing-input case is actually handled.

## Suggested fix

`--seed` already does this correctly one block up (`Program.cs:383-387`): it
checks existence first and throws a message-carrying exception, so it stays
graceful for every flavour of bad path.

```console
$ ./nb --seed /nonexistent/x.jsonl -
Error: seed file not found: /nonexistent/x.jsonl
```

Give the positional program file the same treatment in `BuildProgramAsync`:

```csharp
if (_programFile != null && _programFile != "-" && !File.Exists(_programFile))
    throw new FileNotFoundException($"program file not found: {_programFile}");
```

That covers the missing-directory case, the directory-as-file case (`File.Exists`
is false for a directory), and improves the message — the current graceful path
says `Could not find file '<abspath>'` with no hint that it was the *program*
that could not be read, which matters once `--seed`, `--config` and `--mcp` can
each be the culprit.

Broadening the catch filter to `IOException`/`UnauthorizedAccessException`
instead would also stop the crash, but a pre-check keeps the error message ours
and matches how `--seed` and `--config` already behave.

## Notes

- `--config` with a bad path is already graceful
  (`Error: config file not found: …`, `Program.cs:191`).
- Unrelated, noticed while scoping this: `--mcp` pointed at a nonexistent
  manifest is *silently ignored* — the run proceeds as though the flag were
  absent. That is its own bug, not this one; filed here only as a pointer.
