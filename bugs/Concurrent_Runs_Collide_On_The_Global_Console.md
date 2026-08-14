# Two runs in one process collide on the global console, and the loser does nothing

Status: Open (2026-08-14) — found when a second test class started driving real runs and
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
