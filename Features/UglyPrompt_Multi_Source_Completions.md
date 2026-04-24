# UglyPrompt: Generalized Completion Sources

Status: Proposed

## Problem

UglyPrompt's completion-hint system has two hardcoded guard characters — `/`
for `Commands` and `+` for `Kits` — both anchored to position 0 of the line.
The check lives in `LineEditor.RefreshHint` as literal `text[0] == '/'` and
`text[0] == '+'` branches.

This is fine for slash-commands. It's wrong in two ways for everything else:

1. **nb vocabulary leaks into the library.** "Kits" is an nb concept; no other
   UglyPrompt consumer should have to know about it. The API's named
   `Commands` and `Kits` properties are a design smell — they prescribe
   semantics the library has no business prescribing.
2. **Mid-line triggers are impossible.** The proposed `@`-mention feature
   (`Features/At_Mention_Files.md`) wants `@path` to trigger a hint wherever
   the `@` sits — after whitespace, mid-sentence, anywhere. The position-0
   check can't express "word-start" without a rewrite.

Both problems have the same fix. Generalize the source list; generalize the
anchor rule.

## Proposed API

Replace the two named properties with a single list of sources:

```csharp
public enum TriggerAnchor
{
    LineStart,  // trigger char must be at text[0]
    WordStart   // trigger char must be at text[0] or immediately after whitespace
}

public record CompletionSource(
    char Trigger,
    TriggerAnchor Anchor,
    Func<string, IReadOnlyList<CompletionHint>> Lookup);

public class LineEditor
{
    public List<CompletionSource> Sources { get; } = new();
    // ... rest unchanged
}
```

`Lookup` takes the current token body (everything typed after the trigger,
up to the cursor) and returns matching hints. Static lists become trivial
wrappers; dynamic sources (file completion, recent history, etc.) drop in
without special casing.

### Example usage

nb registers its three sources at construction:

```csharp
editor.Sources.Add(new CompletionSource(
    '/', TriggerAnchor.LineStart,
    prefix => commands
        .Where(c => c.Name.StartsWith("/" + prefix, StringComparison.OrdinalIgnoreCase))
        .ToList()));

editor.Sources.Add(new CompletionSource(
    '+', TriggerAnchor.LineStart,
    prefix => kits
        .Where(k => k.Name.StartsWith("+" + prefix, StringComparison.OrdinalIgnoreCase))
        .ToList()));

editor.Sources.Add(new CompletionSource(
    '@', TriggerAnchor.WordStart,
    prefix => EnumerateFiles(prefix)));
```

An app with no slash commands adds no sources. An app with unique triggers
(`!history`, `?help`, `$var`) registers them without touching the library.

## Behavior details

### Token resolution

On each keystroke, walk from the cursor back through the text looking for a
trigger char whose preceding character satisfies its anchor:

- `LineStart`: trigger is at position 0.
- `WordStart`: trigger is at position 0 **or** immediately preceded by
  whitespace.

If no valid trigger is found, no source is active and the hint line stays
clear (or is cleared if a stale hint is showing). If multiple sources could
match, the closest-to-cursor wins.

The token body is the substring from `trigger_position + 1` up to the cursor.
That body gets passed to `Lookup` verbatim — not `text.Length` — which lets
the user type past a candidate without the hint staying pinned to an earlier
state.

### Rendering

Unchanged. Same single-line strip below the prompt, same ellipsis truncation,
same no-selection display-only semantics. `RenderHintLine` /
`ClearHintLine` don't care which source fired.

### KeyHandler exposure

`KeyHandler` needs to expose the cursor offset so `LineEditor` can do
word-start resolution:

```csharp
public int CursorPosition => _cursorPos;
```

One line. No input-loop changes.

## Migration

This is a breaking change, justifying a **0.3.0** bump.

`Commands` and `Kits` are removed outright — no `[Obsolete]` shims. Rationale:
keeping them around signals "this is still how you do it," which defeats the
clarity win. The migration for existing callers is two lines per source and
can be done mechanically.

### nb's migration

In `Program.cs` (or wherever the editor is constructed), replace:

```csharp
var editor = new LineEditor
{
    Commands = commandHints,
    Kits = kitHints,
};
```

With:

```csharp
var editor = new LineEditor();
editor.Sources.Add(new CompletionSource('/', TriggerAnchor.LineStart,
    prefix => commandHints.Where(h => h.Name.StartsWith("/" + prefix, ...)).ToList()));
editor.Sources.Add(new CompletionSource('+', TriggerAnchor.LineStart,
    prefix => kitHints.Where(h => h.Name.StartsWith("+" + prefix, ...)).ToList()));
```

The `StartsWith` filtering currently happens inside `RefreshHint`; it moves
into the `Lookup` callback. Slightly more verbose at the call site, but
honest about where the logic lives.

## What stays out of v1

- **Tab-to-accept.** Hints remain display-only, matching current behavior.
  Active completion (select-and-insert) requires touching the `KeyHandler`
  input loop and introduces a "selected candidate" concept. Worth doing, but
  separately — pairs naturally with arrow-key navigation through candidates.
- **Per-source rendering style.** All sources share the ambient-strip render.
  A source that wanted a multi-line picker or a sidebar would need its own
  rendering hook. Out of scope.
- **Fuzzy matching.** `StartsWith` remains the filter. Fuzzy is a `Lookup`
  implementation detail if a consumer wants it — the library doesn't
  prescribe.

## Testing

Console line editors are bug-prone in ways that the existing tests don't
catch — soft-wrap interactions, hint strip rendering, cursor placement
relative to actual cell layout. The b27b6b6 fix ("ClearLine leaving
orphans after soft-wrap") is the canonical example: a layout bug nobody
saw until a user hit it, then required a custom 2D-grid test fake to
reproduce. The infrastructure for catching these tests already exists;
we just don't reach for it by default.

### Current state

- `IConsoleAdapter` is the seam — `KeyHandler` and `LineEditor` only
  touch the console through it.
- `KeyHandlerTests.FakeConsole` (`KeyHandlerTests.cs:9-26`) tracks cursor
  position only, not cell contents. Most tests use this.
- `KeyHandlerTests.GridConsole` (`KeyHandlerTests.cs:457-495`, added in
  b27b6b6) tracks cell contents in a 2D array. Used by exactly one test
  — the regression for the bug that motivated it.

The pattern that catches layout bugs only gets reached when someone
already knows about a layout bug.

### `GridConsole` + `Verify.Xunit` — automated layout regressions

Lift `GridConsole` into a shared `UglyPrompt.Tests/Testing/` namespace
and make it the default test fake. Delete `FakeConsole`. Existing tests
swap one constructor and pass unchanged.

Add to the grid fake:

- `Snapshot()` — returns a multi-line string view of visible cells, with
  trailing whitespace trimmed per row. Suitable for snapshot assertions.
- `RowAt(int top)` / `CellAt(int left, int top)` — convenience for
  spot-check assertions.
- `Resize(int w, int h)` — pin a test to specific buffer dimensions
  (essential for soft-wrap tests).
- Optional: `CursorMarker` in `Snapshot()` output, e.g. render the
  cursor cell as `█` so cursor placement shows up in the snapshot.

Add `Verify.Xunit` as a test dependency (single `dotnet add package`
call). Snapshot tests look like:

```csharp
[Fact]
public Task Hint_strip_renders_below_prompt()
{
    var console = new GridConsole { BufferWidth = 40, BufferHeight = 10 };
    var editor = new LineEditor();
    editor.Sources.Add(/* ... */);

    Type(editor, "/he");

    return Verify(console.Snapshot());
}
```

Approved snapshots live in `*.verified.txt` files alongside the test
source. Diffs are immediately reviewable; intentional UI changes
require explicit acceptance via the Verify tooling. This is the closest
.NET gets to "Playwright for terminals."

### `UglyPrompt.Demo` — runnable REPL workbench

A tiny console project (~50 lines) that wires up `LineEditor` with all
three trigger types pre-configured (a couple of `/`-commands, a couple of
`+`-kits, an `@`-source backed by the cwd). Reads lines and echoes them
back with their resolved sources.

Why this matters more than it sounds like it should: UglyPrompt's worst
failure mode is "behaves differently across terminal emulators." Today,
reproducing such bugs requires standing up nb with a real provider — a
lot of friction for a layout glitch. With a demo binary, the whole
reproduction is "download `uglyprompt-demo`, run it in your terminal, do
X." 10x drop in repro cost both for us investigating and for users
filing useful issues.

Bonus: the demo doubles as the README's working example. Instead of a
fake snippet, README points at `Demo/Program.cs` — real, runnable,
CI-tested code.

Manual cross-terminal matrix lives in the new repo's `TESTING.md`:
"for each release, run `uglyprompt-demo` in [Windows Terminal, cmd.exe,
iTerm2, tmux, Alacritty, GNOME Terminal] and verify [paste, history,
hint flicker, soft-wrap, bracketed paste]." Tedious but systematized,
and the demo binary makes each cell take 30 seconds instead of 10
minutes.

### PTY smoke harness — later, optional

For end-to-end scenarios that exercise the real `IConsoleAdapter`
implementation against an actual pty, `Pty.Net` (Microsoft) lets us
spawn the demo binary under a pseudo-terminal, push raw keystroke bytes,
and capture raw stdout. Use case: catching drift between the test fake
and real-`Console` behavior, bracketed-paste handshakes, real ANSI
sequence emission.

Not for every test — slow and brittle. Build a small suite of 5-10
critical scenarios (cold start, multi-source hint dispatch, paste large
blob). Cost: ~2-3 days for the harness, ~30 min per scenario after. Worth
doing once UglyPrompt is its own repo, but not blocking the multi-source
work.

What can't be tested at any layer: real-terminal-specific quirks
(ConPTY vs. iTerm2 vs. tmux pass-through). Those stay manual. The PTY
harness narrows the surface, doesn't eliminate it.

## Implementation plan

The whole sequence is designed as one extended session, with a hard
property: **if anything goes sideways at any step, nb continues to use
its in-tree UglyPrompt 0.2.x copy with zero degradation.** No NuGet push
happens until the very end. No nb code changes until the new package is
proven. The worst case is "we burned a session and feel sad," not "nb
is broken or NuGet is polluted."

### Step A — Stand up the new repo

- Create `github.com/breitreiter/uglyprompt`.
- History-preserving extraction of `UglyPrompt/` and `UglyPrompt.Tests/`
  via `git filter-repo` or `git subtree split` so authorship and the
  b27b6b6-style fix history come along.
- Initial commit on the new repo's `master`. CI scaffold (`dotnet
  build` + `dotnet test`).
- nb is untouched.

### Step B — All the violent work, in the clean repo

Order within the step:

1. Promote `GridConsole` to a shared `UglyPrompt.Tests/Testing/`
   namespace; delete `FakeConsole`. Add `Snapshot()`, `RowAt`,
   `CellAt`, `Resize`, optional cursor marker.
2. Add `Verify.Xunit` as a test dependency.
3. Add `UglyPrompt.Demo` console project. Wire up `/`, `+`, and `@`
   sources against demo data + cwd. README updated to reference it.
4. Implement the new `CompletionSource` / `TriggerAnchor` / `Sources`
   API. Expose `KeyHandler.CursorPosition`.
5. Rewrite `RefreshHint` to walk the cursor backward and dispatch.
6. Snapshot tests for: line-start triggers, word-start triggers,
   mid-token typing past a candidate, hint clearing on token loss,
   soft-wrap interactions with the hint strip.
7. Bump to 0.3.0 in csproj. Update README to reflect the new API.

Everything in this step is reversible — the new repo is the only place
affected, and we haven't pushed to NuGet yet.

### Step C — Cross-terminal validation via the demo

Run the demo binary through the manual matrix (Windows Terminal,
cmd.exe, iTerm2, tmux at minimum). Fix anything weird. Iterate inside
Step B if the rewrite needs adjustments.

If anything here is broken or feels off, **stop**. The new repo sits
unpublished; nb still uses 0.2.x; no harm done.

### Step D — Publish 0.3.0 to NuGet

`dotnet pack -c Release` from the new repo. `dotnet nuget push`. Tag
the release in git. This is the first irreversible step — a published
NuGet package can be unlisted but not deleted.

### Step E — nb migration

In nb:

- Add `<PackageReference Include="UglyPrompt" Version="0.3.0" />` to
  `nb.csproj`.
- Remove the `<ProjectReference>` to the local subdir.
- Delete `UglyPrompt/` and `UglyPrompt.Tests/` from the nb repo.
- Update `nb.sln` to drop the removed projects.
- Update `Program.cs` (or wherever the editor is constructed) to use
  the new `Sources` API. See "Migration" section above.
- Build, test, smoke.
- Commit. Push.

### Failure modes and what to do

- **Step A fails** (history extraction broken or messy): redo the
  extraction with different filter-repo settings. nb unaffected.
- **Step B fails** (API rewrite breaks something we can't easily fix):
  delete the new repo or shelve it. nb unaffected.
- **Step C fails** (cross-terminal regressions we can't squash): same
  as B — shelve, fix later, nb unaffected.
- **Step D fails** (NuGet push errors out): retry with a `0.3.1` if
  `0.3.0` somehow got partially published. Worst case, we have a
  cosmetically-skipped version number on NuGet.
- **Step E fails** (nb doesn't compile against 0.3.0): revert the nb
  changes. The 0.3.0 package is live for other consumers; nb stays on
  its in-tree 0.2.x temporarily until we figure out the migration.

The first three failure modes have zero blast radius. The last two are
the only ones that publish anything, and even they degrade gracefully.

### Where At-mention work fits

`Features/At_Mention_Files.md` Phase 1 (backend-only `@`-token expansion
on submit) doesn't depend on UglyPrompt at all — can ship before, after,
or alongside this work. Phase 2 (the picker UI) needs the new
`Sources` API and so happens after Step E.

## Open questions

1. **Anchor granularity.** `WordStart` treats "preceded by whitespace" as the
   boundary. Is that right, or do we want "non-alphanumeric"? The latter
   would let `foo@bar.com` trigger the `@` source mid-token, which is
   probably wrong for file references but might be right for something else.
   Punt to the source: let `Anchor` be a callback `Func<char prev, bool>` if
   we end up needing it. Don't over-engineer v1.

2. **Empty-token behavior.** When the user types just the trigger with
   nothing after (`@`, `/`, `+`), do we show all candidates or no hint?
   Current behavior shows all (empty prefix matches everything). Keep it.

3. **Case sensitivity.** Current matching is `OrdinalIgnoreCase`. Keep
   uniformly, document it, let sources override by implementing their own
   `Lookup` if they care.

## References

- `UglyPrompt/LineEditor.cs` — current hint dispatch (lines 145-173).
- `UglyPrompt/KeyHandler.cs` — `_cursorPos` state that needs exposure.
- `Features/At_Mention_Files.md` — the feature this unblocks.
