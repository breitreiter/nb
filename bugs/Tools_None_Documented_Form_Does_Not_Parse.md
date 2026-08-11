# `tools none +read_file` is documented but does not parse

Status: Open (2026-08-11) — found while writing the first program for a harness
that drives nb against a containerized fixture.

## Symptom

`docs/conversation-program-cli.md` §5.2 documents the combined form and gives it
as the worked example:

> Tokens are `+name`, `-name`, or the lone `none` (reset/clear). … `tools none
> +read_file` allows just that one.

That line does not parse:

```
$ cat trial.nb
tools none +read_file +write_file +edit_file +list_dir +find_files +grep +bash

$ nb --validate trial.nb
Error: line 4: 'tools' tokens must be +name, -name, or 'none' — got 'none'.
```

## Workaround

Split it across two directives. This is equivalent and does parse:

```
tools none
tools +read_file +write_file +edit_file +list_dir +find_files +grep +bash
```

## Why it is worth fixing

The documented one-liner is the natural way to express "expose exactly this
set", which is the common case for a headless run. The error message is also
slightly misleading: it says `none` is not an accepted token while quoting
`'none'` as one of the accepted tokens, so the reader's first assumption is a
typo in their own line rather than a positional restriction.

Either accept the combined form, or document that `none` must be alone on its
own `tools` line and reword the error to say so — something like *"'none' must
be the only token on a `tools` line; put the additions on a following line"*.

## Related, from the same session

Two more things about the tool-permission surface that surprised a first-time
user. Neither is obviously a bug; both are undocumented.

**Approval `bash` patterns match the whole command string, so an allow-rule for
a program does not cover that program used in a compound command.** With:

```
approval default deny
approval bash go
approval bash go *
```

these were all refused, because none of them *start* with `go`:

```
cd /work && go mod tidy 2>&1
cd work && go mod tidy
```

That is defensible — a rule matching anywhere in the string would be trivially
escapable — but the docs describe the value only as "a command pattern
(glob-ish)", which reads as if it matches the invocation rather than the line.
Worth stating explicitly, because the failure mode is a run that dies on
`tool_error_limit` after the model retries a reasonable-looking command three
times.

**Read-only bash appears to be auto-approved.** In the same run, with the policy
above, `ls -la <path>` executed and returned output while the `cd … && go …`
calls were refused with *"bash (Run) was denied"*. The `(Run)` suggests a
classification of bash calls into read-only and mutating, with only the latter
gated. If that is intentional it belongs in §5.3 — it materially changes what
`approval default deny` promises.
