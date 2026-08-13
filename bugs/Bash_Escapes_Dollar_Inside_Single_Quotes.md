# `bash` tool escapes `$` unconditionally, breaking every single-quoted script

Status: Open (2026-08-12) — found in a headless harness run against a
containerized Ruby fixture, at `61b5a65`.
**Severity: high.** Any command that passes a `$` through to another
interpreter is unrunnable, and the failure looks like the model wrote bad code.

## Symptom

```
model: bash { command: "ruby -e 'puts $LOAD_PATH'" }
result:
  -e:1: syntax error, unexpected backslash
  puts \$LOAD_PATH
       ^
  ruby: compile error (SyntaxError)
```

The backslash is not in the model's command. It is inserted by nb.

In the run where this was found, the model retried six times — `ruby -e`,
`bundle exec ruby -e`, `puts` → `p`, with and without a trailing pipe — because
the error names a syntax problem in *its own* argument, which is normally the
model's fault and normally fixable by rewriting. It is not fixable: every
rewrite that keeps the `$` fails identically. Those six calls were ~10% of the
run's tool budget.

## Mechanism

`nb.Core/Shell/BashTool.cs:236` builds the child process arguments as a single
pre-quoted string:

```csharp
psi.FileName = _env.ShellPath;
psi.Arguments = $"-c \"{EscapeBash(command)}\"";
```

and `EscapeBash` (`:239`) escapes for that outer double-quoted context:

```csharp
return command
    .Replace("\\", "\\\\")
    .Replace("\"", "\\\"")
    .Replace("$", "\\$")
    .Replace("`", "\\`");
```

The escaping is correct for the wrapper it builds — `bash -c "…"` genuinely
would interpolate `$LOAD_PATH` otherwise. The bug is that the escape is applied
to the **whole command text**, including regions where bash would not have
interpolated anyway. Inside `'single quotes'` a `\$` is not an escaped dollar;
it is a backslash followed by a dollar, and both characters are passed through
to whatever reads that argument.

So the transform is not round-trip safe: it prevents interpolation in the
one place it must, and corrupts the string everywhere else.

## Blast radius

Every idiom that hands a `$` to a second interpreter:

| | |
|---|---|
| `ruby -e 'puts $LOAD_PATH'` | broken |
| `rails runner 'puts $0'` | broken |
| `awk '{print $1}'` | broken |
| `perl -ne 'print $_'` | broken |
| `sed 's/foo$//'` | broken |
| `grep 'x$' file` | broken |
| `jq '.[] | .$k'`, `psql -c '… $1 …'` | broken |

`awk '{print $1}'` is the one to worry about: it does not error, it prints the
wrong field, and nothing in the transcript says why.

## Fix

The sandboxed path in the same method already does this correctly, and says so
in its own comment — *"ArgumentList passes the command literally, so no
bash-escaping"*. Use the same mechanism unsandboxed:

```csharp
psi.FileName = _env.ShellPath;
psi.ArgumentList.Add("-c");
psi.ArgumentList.Add(command);
```

`ArgumentList` quotes each entry for the platform's process-creation API, so the
command arrives at bash as one argv entry, byte-for-byte. `EscapeBash` can then
be deleted rather than corrected — there is no correct version of it, because
deciding what to escape requires parsing the shell grammar, which is what bash
is for.

Windows/Git Bash is worth a check: .NET quotes `ArgumentList` entries with the
Win32 `CommandLineToArgvW` rules, and bash's own parsing of `-c` differs. The
bwrap path already relies on this working, so if it is wrong it is wrong today.

## Test

```
bash { command: "ruby -e 'puts $LOAD_PATH'" }   # must not contain a backslash
bash { command: "echo \"$HOME\"" }               # must NOT expand — or must, but
                                                 # pick one and pin it in a test
```

The second case is the one that will decide the fix: with `ArgumentList`, a
double-quoted `$HOME` **will** expand, because bash sees the real command. That
is the correct shell semantics and almost certainly what a model expects, but it
is a behaviour change from today's "nothing ever expands", so it deserves a test
that states the intent rather than a silent flip.
