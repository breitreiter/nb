# Publishing nb for distribution: four gotchas, one of them a secret leak

Status: Open (2026-08-13) — found moving a research harness's agent inside its
fixture container, so nb ships as a bind-mounted self-contained binary rather
than running on the host. Against `61b5a65` + the `ArgumentList` fix.

These are mostly **documentation** findings. §"Building for distribution" in
`README.md` is six lines, and every one of them is true; the trouble is what
sits just outside them. Ordered by what they cost.

---

## 1. `appsettings.json` — including live API keys — is copied into the publish output

**Severity: high.** This is the only one here with a security consequence.

`dotnet publish` copies the repo's `appsettings.json` into the output directory.
That is the file holding `Search.ApiKey` and every `ChatProviders[].ApiKey`. So
the artifact you built to *distribute* contains your own credentials, and nothing
in the build output says so.

It bit here because the publish directory is mounted into a container the agent
under test can read. An agent asked to poke around would have found live keys at
a fixed path. In a distribution scenario it is worse: the keys ship.

**Fix.** Two parts, and the docs half matters more than the build half:

- `README.md` §"Building for distribution" already tells the reader to ship
  `mcp.json` and `theme.json` **alongside** the executable. It should say in the
  same breath that `appsettings.json` is copied **into** the output and is
  probably not the one you want — with the one-line remedy (`rm` it, or
  `--config`, or `appsettings.example.json` under a `Condition`).
- Better still, stop copying it: publish `appsettings.example.json` as
  `appsettings.json`, or exclude it from the publish item group. A build that
  cannot leak a key is worth more than a note saying it might.

---

## 2. Publishing the *project* silently yields no providers

**Severity: medium**, and the failure is far from the cause.

> **This one recurs.** Joseph reports hitting it roughly half a dozen times
> (2026-08-13) — so it is not a newcomer's stumble that documentation alone
> fixes. Someone who already knows the answer keeps paying for it, which is the
> signature of a missing build-time signal rather than a missing paragraph.
> Weight the `CopyProvidersAfterPublish` warning below accordingly: it is the
> fix, and the README note is the consolation prize.

```bash
dotnet publish nb.csproj -c Release -r linux-x64 --self-contained -o /out
# builds fine, exits 0
/out/nb --config x.json prog.nb
#   Entry 'LocalCoder' names provider implementation 'LocalLlm' (via "Provider"),
#   which is not loaded
#   Loaded implementations: (none)
```

**The README's own command is correct** — it publishes the *solution* (no
project argument), which builds every provider for the RID and lets
`CopyProvidersAfterPublish` find them. This report is not that the documented
command is broken. It is that the *undocumented neighbouring* command, which a
reader reaches for the moment they want a smaller artifact, fails silently at
build time and loudly at runtime an hour later.

**Mechanism.** Both the provider projects' `ProviderOutputPath` and nb's
`CopyProvidersAfterPublish` target are written in terms of `$(SolutionDir)`,
which is empty when a project is built on its own. The copy then reads from a
path under the current directory, finds nothing, and copies nothing — with no
warning, because copying zero files is not an error.

Compounding it: `dotnet build nb.sln -r linux-x64` is rejected outright
(`NETSDK1134: Building a solution with a specific RuntimeIdentifier is not
supported`), so a reader who hits problem 2 and reasonably tries "then let me
build the solution for the RID first" hits a hard error and is out of obvious
moves. The working incantation is two commands, neither of them guessable:

```bash
dotnet build Providers/LocalLlm/nb.Providers.LocalLlmProvider.csproj \
  -c Release -r linux-x64 -p:SolutionDir="$PWD/"
dotnet publish nb.csproj -c Release -r linux-x64 --self-contained \
  -p:SolutionDir="$PWD/" -o /out
```

**Fix, in priority order.**

1. **Make `CopyProvidersAfterPublish` warn when it copies zero provider
   directories.** The target already knows the answer at the moment the mistake
   is made; it just says nothing. A build that produces a binary guaranteed to
   fail at startup should say so while the reader is still looking at the build.
   Given how often this recurs, this is the whole fix.
2. Consider erroring instead of warning when `RuntimeIdentifier` is set and the
   provider source directory does not exist — that combination has no legitimate
   reading.
3. Then the README: distribution builds publish the **solution**, and publishing
   the project alone needs `-p:SolutionDir=` plus a per-provider build.

A third option worth weighing: make the paths independent of `$(SolutionDir)` —
`$(MSBuildThisFileDirectory)` resolves the same way whether a project or a
solution is being built, which would delete the failure mode rather than report
it. That is a larger change to the provider projects, but it is the only one that
also fixes the person who never reads the warning.

---

## 3. Publishing the solution puts the test platform in the distribution output

**Severity: low**, but it is the reason someone reaches for problem 2.

```
$ dotnet publish -c Release -r linux-x64 --self-contained -o /out   # the documented command
  nb -> /out/
  nb.Tests -> /out/
$ ls /out | grep -c TestPlatform
  5
```

`nb.Tests` and `mcp-servers/mcp-tester` publish into the same directory: xunit,
`Microsoft.TestPlatform.*`, `mcp-tester.dll`. Harmless to run, but it means the
documented distribution artifact contains the test harness — and a reader who
notices will do the natural thing, publish `nb.csproj` alone, and land in
problem 2.

**Fix.** `<IsPublishable>false</IsPublishable>` on `nb.Tests` (and on
`mcp-tester` unless shipping it is deliberate).

---

## 4. Minimal containers have no libicu, and .NET aborts rather than degrading

**Severity: medium** for anyone containerizing; invisible otherwise.

```
$ podman run --rm -v /out:/nb:ro golang:1.25 /nb/nb
Process terminated. Couldn't find a valid ICU package installed on the system.
Aborted (core dumped)
```

Language-toolchain images (`golang`, and plenty of slim Debian bases) carry no
`libicu`, and self-contained .NET **FailFast**s on startup rather than falling
back. The runtime's own message names the fix, which is the only reason this
isn't higher — but it costs a round trip for someone who has no reason to think
of nb as a .NET application at all.

**Fix.** One line in the README next to the publish command:

> Running in a minimal container? Set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`
> or install `libicu` — self-contained .NET aborts at startup without it.

Alternatively set `<InvariantGlobalization>true</InvariantGlobalization>` in
`nb.csproj` and be done, if nb has no culture-sensitive comparisons that matter.

---

## 5. A bad approval value is reported *after* the run, not before it

**Severity: medium.** Separate from publishing, found in the same sitting.

`approval default allow` does not exist — §5.3 correctly documents the value set
as `prompt | deny`, and that is squarely a reading failure on my part. The bug is
what happens next:

```
approval default allow
```
```
… 60 tool calls, every bash call denied …
program: approval default 'allow' unknown (prompt | deny) — ignored
```

The warning is emitted **as the last line of the run**. In between, the program
ran with the directive dropped, which under the `prompt` default and a non-TTY
means every bash call is refused. A full token budget bought nothing, and the
transcript reads like a model that cannot use tools — the diagnosis is at the
bottom of a page of denials, after the evidence that made it look like something
else.

`nb --validate` catches this properly (`invalid approval default 'allow'`,
exit 1), so the check exists; it just isn't reached on the path people use.

**Fix.** Emit the warning when the directive is parsed, before the first `run`.
Better: refuse. An approval directive nb cannot honour is a *safety* directive
being silently dropped — the failure here was harmlessly restrictive, but the
same code path silently drops an ignored value that was meant to be permissive
or restrictive alike, and there is no reason to guess which. `--validate` already
treats it as an error; the runtime disagreeing with `--validate` about what is
valid is the part worth closing.

---

## What would have prevented all five

A `docs/deployment.md` (or a longer README section) covering the case nb clearly
supports but does not describe: **shipping nb as a standalone binary somewhere
that is not a developer's checkout.** It needs perhaps thirty lines — the
solution-vs-project publish, providers, the `appsettings.json` warning, the
container environment variable, and the fact that `--config` is how you point a
deployed binary at a configuration it did not ship with.
