---
kind: bug
title: 'Two runs in one process collide on the global console, and the loser does nothing'
created: 2026-08-14
updated: 2026-09-05
status: current
state: fixed
severity: medium
cluster: library-host
---

# Two runs in one process collide on the global console, and the loser does nothing

Status: **Fixed 2026-09-05** — both parts, plus a second collision found alongside.
Originally: Open (2026-08-14) — found when a second test class started driving real runs and
the existing golden-master tests began failing intermittently with *"the model was never
invoked."*

## Symptom

Two `ConversationManager.RunAsync` calls in flight concurrently in one process. One
completes normally. The other returns having **never called the `IChatClient`** — no
model round-trip, no tool calls, no error surfaced to the caller. `LastOutcome` is set
as though the turn ended cleanly.

Intermittent, and it presents as a product bug rather than a collision: the run simply
did nothing.

## Cause

`SendMessageInternalAsync` wraps the first round-trip in a Spectre live display for the
"Thinking…" spinner (`nb.Core/ConversationManager.cs:388-391`):

```csharp
var hasMore = await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse(UIColors.SpectreMuted))
    .StartAsync("Thinking...", async _ => await enumerator.MoveNextAsync());
```

`AnsiConsole` is process-global and permits one live display at a time; a second
concurrent `StartAsync` throws. The throw lands in the turn's own `try` (opened at
`:339`), which handles it as a failed turn — so the enumerator is never advanced and the
client is never called.

The spinner is *chrome*, and the exception it raises is being treated as a *model
failure*. That conflation is the actual defect: chrome should not be able to cancel a
model call.

## Why it matters beyond the tests

Tests were only the messenger. `Nb.RunAsync` is a documented in-process library entry
point ("one contract, three surfaces"), and nothing about it says single-threaded. A host
that fans out several evaluations in one process — the obvious way to run a matrix of
programs, and the exact shape of harness-emulation A/B work — hits this. `NbRuntime`
already suppresses chrome for library hosts, but not this call.

The stateless design makes concurrency safe everywhere else: no history file, no lock,
no per-directory state. This one static is the exception.

## Fix

Two independent parts, in order of value:

1. **Chrome must not be able to fail a run.** Wrap the status display so a live-display
   collision degrades to no spinner rather than to a lost turn — awaiting
   `enumerator.MoveNextAsync()` directly on that path.
2. **Do not open a live display when chrome is suppressed.** `NbRuntime` already knows
   it is a library host; the spinner should be gated on the same flag as the rest of the
   chrome, which fixes the common case outright.

## Workaround in place

`nb.Tests/ConsoleBoundCollection.cs` puts every test class that drives a run into one
xunit collection, so they serialise. It is a real fix for the tests and no fix at all
for a library host. Delete it when the above lands.

## Verification

Two `RunAsync` calls started concurrently against a recording `IChatClient` must both
reach the client. That reproduces it today with no model needed.

---

## Fix (2026-09-05)

**Part 1, as specified: chrome cannot fail a run.** The spinner call moved behind
`ConversationManager.WithThinkingSpinnerAsync`, which claims the process's single
live-display slot with an interlocked flag. The loser runs *without a spinner* instead of
losing its turn.

The flag is doing something a `try/catch` around `StartAsync` could not. Catching cannot
distinguish *"the display refused to open"* from *"the work threw"*, and retrying on the
latter would issue a **second model call** — turning a chrome bug into a double-billed
round-trip. Claiming the slot up front never runs the work twice. It is also exact rather
than heuristic: this is the only live display in the codebase (`grep` for
`AnsiConsole.Status|Live|Progress` finds one call site), so the flag and the real slot
cannot disagree.

**Part 2 folded into part 1.** The report asks for the spinner to be gated on the
chrome-suppressed flag as well. That turned out to be unnecessary: a suppressed-chrome
host writes to `TextWriter.Null`, and with the slot claimed correctly a live display on a
null writer is harmless. Adding a second gate would have been a second thing to keep in
sync with no behaviour to show for it.

## A second collision, found while fixing this one

`Nb.RunAsync` redirects the process-global `AnsiConsole.Console` to the caller's
diagnostics sink and restored it from a plain local:

```csharp
var savedConsole = AnsiConsole.Console;   // A saves the real console
AnsiConsole.Console = …;                  // B then saves *A's* console
finally { AnsiConsole.Console = savedConsole; }
```

Two overlapping runs each save what the other set, so the last one out restores a writer
belonging to a **finished run**. The process console is then permanently pointed at a
dead sink — and unlike the spinner bug, this outlives the runs: every later
`AnsiConsole` write in that process, including a CLI's, goes nowhere.

Confirmed before fixing: six concurrent `Nb.RunAsync` calls, then
`Assert.Same(original, AnsiConsole.Console)` — red.

The swap is now refcounted under a lock: first in redirects, last out restores, nobody
restores a stale value.

**Residual, stated rather than hidden.** Concurrent hosts now share the *first* one's
`DiagnosticsWriter`, because one global cannot serve two sinks. That is a real limitation
and it is still an improvement, because the previous behaviour was not "correct routing"
— it was A's own diagnostics escaping to the real console mid-run once B swapped, *plus*
the permanent corruption. Giving each run its own sink needs the reporter seam that stops
engine classes writing to a global at all (`TODO.md`, *"engine chrome still lives in
nb.Core"*), which is the same root this report already names.

## Tests

Three in `nb.Tests/ConcurrentRunTests.cs`, all confirmed failing first.

The race is made **deterministic rather than probable**, which is why this got a real
red test where the triage plan predicted none was possible. A gated `IChatClient` blocks
inside the first run's streaming call, so that run provably owns the live display before
the second starts; the second therefore always meets an open display, which is the losing
condition. No sleeps, no timing margin, nothing to flake.

**The load-bearing verification is the deletion, though.** `ConsoleBoundCollection` — the
xunit collection that serialised every run-driving test class — is **gone**, along with
its ten `[Collection]` attributes. Ten classes now race for real, and the suite is green
across repeated runs. That is a stronger signal than the three tests, and it is what the
triage plan named as the real one.

Side effect worth having: the suite dropped from ~37s to ~25s, because those ten classes
now run in parallel instead of one at a time.
