# A missing `--mcp` manifest is silently ignored, and the run still exits 0

Status: Fixed (2026-08-12) — both gaps closed, as the report recommended.

## Fix

**Gap 1 — explicit manifest, explicit error.** New
`McpManager.ReadExplicitManifest` is the strict path for `--mcp`: missing throws
`FileNotFoundException`, malformed JSON and unreadable files throw
`InvalidOperationException` naming the path. `LoadConfig` now routes an explicit
`manifestPath` through it and keeps the swallowing `try/catch` only for the
layered lookup, where a missing layer is the normal case. `NbRuntime.BuildAsync`
translates both into `NbStartupException` (the shape `Program.cs` already
reports as `Error: …` + exit 1); `--dump-tools`, which calls `LoadConfig`
directly, got the same handling rather than reporting an empty tool surface.

**Gap 2 — assert on named-but-absent, not just named-and-failed.**
`AssertServersAvailable` now also throws when a name in `surface.McpServers` is
missing from the loaded config, with a message distinguishing the two causes:

```console
$ ./nb --mcp /nonexistent/d/m.json q.nb   → Error: MCP manifest not found: /nonexistent/d/m.json
$ ./nb c.nb   # mcp +no-such-server, valid mcp.json
Error: MCP server 'no-such-server' was requested (mcp +no-such-server) but is not configured in mcp.json
```

Both exit 1. Failed-to-start is checked first, so a configured-but-broken server
still reports *why* it broke rather than being relabelled "not configured".

Incidental: the `_config.McpServers.Count > 0 ? … : _config.Servers` selection
was written out at all three use sites; collapsed onto one `ConfiguredServers`
property, since the new assertion needed a fourth.

Regression coverage in `nb.Tests/McpManagerTests.cs` — six explicit-manifest
cases (missing, missing directory, directory-as-file, malformed, valid,
`LoadConfig` end-to-end) and four for the assertion (absent throws, configured
does not, null surface asserts nothing, empty surface + empty config stays
legal, per the strict-empty rule in the Notes below).

Four of these were confirmed failing against the unfixed code, plus the
absent-server assertion test — five in total. `ExplicitManifest_Malformed` has
no pre-fix counterpart to fail against: it exercises a method that did not exist
before, and the old `LoadConfig` bare `catch` it replaces is covered by
`LoadConfig_ExplicitMissingManifest_Throws`, which did fail.

The report's third suggestion — decide whether `EnsureServersConnectedAsync`
should be deleted or wired up — resolved as **deleted**. It had no callers, and
its "not found in mcp.json" warning was the weaker version of the hard-fail that
gap 2 now enforces; keeping a dead path that *looks* like it handles the absent
case is exactly what misleads the next reader. The only surviving mention is in
`plans/onboarding-and-kit-ux.md:72`, describing the removed kit/`CommandProcessor`
system — stale for other reasons, and plan docs record intent, not behavior.

---

Status: Confirmed (2026-08-12) against `bin/Debug/net10.0/nb` at master 61b5a65.
Found while scoping `bugs/Bad_Program_Path_Crashes_With_Stack_Trace.md`.

## What happens

`--mcp` pointed at a path that does not exist is treated as "no MCP servers
configured". The program runs to completion with none of the tools it asked for,
exits 0, and prints nothing to say so.

Program under test (`q.nb`):

```
mcp +built-in-tester
run MOCK:response=hi
```

With the normal layered `mcp.json` — the server is configured but fails to
start, and nb behaves exactly as documented:

```console
$ ./nb q.nb --output jsonl; echo "exit=$?"
Error: MCP server 'built-in-tester' was requested (mcp +built-in-tester) but failed to start: The server shut down unexpectedly.
exit=1
```

Same program, same directive, with a nonexistent manifest:

```console
$ ./nb --mcp /nonexistent/d/m.json q.nb --output jsonl; echo "exit=$?"
{"type":"user","turn":0,"text":"MOCK:response=hi"}
{"type":"assistant_text","turn":1,"text":"I see you've sent \"MOCK:response=hi\" …"}
{"type":"result","turn":null,"exit_reason":"ok","usage":{…},"turns":1,"tool_calls":0}
exit=0
```

No error, no warning, no non-zero exit — a clean-looking transcript from a run
whose declared tool surface never materialised.

## Why this is the wrong shape

This is the exact case `docs/conversation-program-cli.md:409` promises to catch:

> **`mcp +server` naming a server that failed to start** → hard-fail, exit 1 (the
> program selected tools that will never arrive).

…and the case `AssertServersAvailable`'s own doc comment names as its reason to
exist: *"a program that selects a server it needs must not silently run without
those tools."* A typo'd `--mcp` path lands in precisely that situation and slips
through the gate.

It is worse for `--mcp` than it would be for the layered lookup, because `--mcp`
exists to make a run **hermetic**. A harness that pins its manifest per
invocation gets a green exit and a plausible transcript for a run that silently
had no tools — a result that looks comparable to a good run and isn't.

## Root cause — two independent gaps

**1. `LoadConfig` swallows every exception** (`nb.Core/MCP/McpManager.cs:36-40`):

```csharp
public void LoadConfig(string? manifestPath = null)
{
    try { _config = LoadMcpConfiguration(manifestPath); }
    catch { _config = new McpConfig(); }
}
```

A bare `catch` mapping *any* failure to an empty config is defensible for the
layered lookup, where missing files are the normal case and are already skipped
individually by `MergeLayers`. It is not defensible for an explicit
`manifestPath`: the user named that file, so unreadable/missing/malformed are
all errors, not "no servers". Note this also swallows a **malformed** explicit
manifest — a JSON syntax error degrades to empty just as quietly.

**2. `AssertServersAvailable` only inspects `FailedServers`**
(`nb.Core/ConversationManager.cs:204`), which `ConnectAllAsync` populates from
servers that were configured and then failed to connect. A server that is
*absent from the config entirely* is never attempted, never recorded, and so
passes the assertion.

Gap 2 is reachable on its own, without `--mcp` — naming a server that is not in
a perfectly valid `mcp.json` is equally silent:

```console
$ cat c.nb
mcp +no-such-server
run MOCK:response=hi
$ ./nb c.nb --output jsonl; echo "exit=$?"
… normal transcript …
exit=0
```

So gap 2 is the deeper defect; a bad `--mcp` path is one route into it, since
emptying the config turns every named server into an absent one.

There *is* a "not found in mcp.json" warning in the codebase
(`McpManager.cs:54`, in `EnsureServersConnectedAsync`), but that method has no
callers outside `McpManager` — every live path (`Program.cs:172`,
`Program.cs:249`, `nb.Core/Facade/Nb.cs:48`) uses `ConnectAllAsync`. The
diagnostic exists and is dead.

## Suggested fix

Both gaps are worth closing; either alone leaves a silent hole.

- **Explicit manifest, explicit error.** In `LoadMcpConfiguration`, when
  `manifestPath` is non-empty, let a missing or malformed file throw with a
  message naming the path, and stop `LoadConfig` from catching it for that case.
  This matches `--config`, which already reports `Error: config file not found: …`
  and exits 1 rather than falling back to defaults.
- **Assert on named-but-absent, not just named-and-failed.** Have
  `AssertServersAvailable` also fail when a name in `surface.McpServers` is
  absent from the loaded config, with a message distinguishing the two ("not
  configured" vs "failed to start"). That closes the standalone typo case and
  makes the gate mean what its doc comment says.

Once named-but-absent hard-fails, a bad `--mcp` path fails loudly even for
programs that reach it through gap 2 — but the explicit-manifest error is still
worth having, since it names the actual mistake instead of blaming the server.

## Notes

- A program that names **no** servers should keep working with an empty config;
  MCP is strict-empty until `mcp +server` (`docs/conversation-program-cli.md:397`),
  so neither fix should disturb that.
- `--dump-tools` goes through `ConnectAllAsync` too, so a bad `--mcp` path
  currently makes it report an empty tool surface rather than an error.
- Worth deciding whether `EnsureServersConnectedAsync` should be deleted or
  wired up; leaving a dead warning path invites the next reader to assume the
  absent-server case is handled.
