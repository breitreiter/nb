# `approval search` is documented but the program parser rejects it

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

## Adjacent, while you are in there

`docs/conversation-program-cli.md` §5.2 lists the native tool names as
`bash, read_file, write_file, edit_file, find_files, grep, list_dir,
apply_patch, fetch_url, todo` — **`search_web` is missing**, though
`ConversationManager.cs:102` includes it. Someone reading only §5.2 would not
know the tool exists.
