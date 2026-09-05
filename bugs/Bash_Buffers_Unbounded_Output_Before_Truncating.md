---
kind: bug
title: '`bash` buffers a command''s entire output in memory, then throws away 99% of it'
created: 2026-08-13
updated: 2026-09-05
status: current
state: fixed
severity: medium
cluster: bash-boundary
---

# `bash` buffers a command's entire output in memory, then throws away 99% of it

Status: **Fixed 2026-09-05** — both halves, as suggested. See *Fix* at the foot.
Originally: Open (2026-08-13) — hit in a headless trial where a tool under test spun
on a closed stdin. Against `61b5a65` + the `ArgumentList` fix.
**Severity: medium** — bounded by the OS, recovered from in practice, but the
recovery leans on catching `OutOfMemoryException`, which is not a thing to lean on.

## Symptom

```
model: bash { command: "echo \"y\" | bundle exec <installer> <key> 2>&1" }
result: Error executing command: Exception of type 'System.OutOfMemoryException' was thrown.
```

The command is a runaway producer: the installer re-prints its interactive prompt
in a tight loop when stdin is at EOF, at roughly 20 MB/s. nb accumulated it until
the process ran out of memory.

**Credit where it's due:** the exception was surfaced as an ordinary tool error,
the turn continued, and the model adapted and finished its task by another route.
The blast radius was one tool call. That is the right behaviour and this report
is not asking for it to change — only for the buffer that made it necessary.

## Mechanism

`BashTool.ExecuteAsync` collects every line before deciding what to keep:

```csharp
var stdoutLines = new List<string>();
…
stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, cts.Token);
…
var (stdout, stdoutTruncated) = ApplySandwich(stdoutLines);
```

and `ReadLinesAsync` (`:163`) is an unbounded append loop:

```csharp
while (!ct.IsCancellationRequested)
{
    var line = await reader.ReadLineAsync(ct);
    if (line == null) break;
    lines.Add(line);
}
```

`ApplySandwich` runs *after* the process exits and keeps
`sandwichHeadLines + sandwichTailLines` — **70 lines by default** (50 + 20). So
peak memory is the size of the whole output, to produce a result that can never
exceed about 70 lines. Everything in between is allocated, retained, and dropped.

Two things make the constant worse than the byte count suggests:

- **`List<string>` of short lines is the pathological shape.** A runaway prompt
  loop emits many tiny lines; each becomes a separate `string` object with its
  own header and length field, plus a slot in the backing array. Managed heap
  cost runs several times the raw byte count. Here ~1.2 GB of output reached a
  2 GB container limit.
- **The timeout does not bound this.** `BashTimeoutSeconds` defaults to 300 in
  this harness's config; at 20 MB/s that is ~6 GB before the cancellation fires.
  The timeout bounds *duration*, and memory is the resource actually at risk.

`totalBytes` is computed from the same list afterwards, so the reported size is
correct — the information the caller gets is fine. It is the retention that isn't.

## Why it matters beyond one weird installer

The general case is "the model runs something that produces more output than
anyone expected", which is routine: a verbose build, `find /`, a test suite with
per-assertion logging, a `curl` of something large, any interactive tool reached
without a TTY. nb's design already says these outputs are not worth keeping —
truncation is deliberate and documented in the tool description (*"Large outputs
are truncated"*). The buffer just doesn't act on that decision until it is too
late to help.

Without a container limit the ceiling is host RAM, and the process that dies may
not be nb.

## Fix

Bound the buffer at read time instead of after exit. `ApplySandwich` already
defines exactly what is needed — the first `head` lines and the last `tail` lines
— and both are computable in a single pass with fixed memory:

```csharp
// keep the first N; keep the last M in a ring; count the rest
if (kept.Count < _sandwichHeadLines) kept.Add(line);
else { tail.Enqueue(line); if (tail.Count > _sandwichTailLines) tail.Dequeue(); }
omitted++;
totalBytes += Encoding.UTF8.GetByteCount(line) + 1;
```

Memory becomes O(head + tail) — a few KB — regardless of how much the child
emits, `totalBytes` stays exact because it accumulates as it goes, and
`ApplySandwich` becomes the formatter it already almost is.

Worth adding alongside: a byte ceiling per call that stops reading and reports
`[output limit reached, process killed]`, so a producer that will never be read
is not left running for the rest of the timeout. That turns the runaway case into
a clean, legible tool result instead of an `OutOfMemoryException` the model has
to interpret.

## Test

Fixed-memory behaviour is observable without a real runaway:

```
bash { command: "yes hello | head -c 50000000" }     # 50 MB, ~7M lines
```

Assert the result is the usual sandwich, that `totalBytes` reports ~50 MB, and —
the point of the change — that peak managed heap during the call stays flat.
`GC.GetTotalAllocatedBytes` before/after is a serviceable proxy in a test.

---

## Fix (2026-09-05)

Both suggestions taken: the single-pass bounded collector, and the byte ceiling.

**`OutputCollector`** replaces the `List<string>` per stream. It keeps the first
`headCap` lines, the last `tailCap` in a `Queue<string>` ring, and accumulates
`TotalLines`/`TotalBytes` as lines arrive. `ApplySandwich` becomes the formatter it
already almost was, reading head and tail off the collector instead of slicing a
retained list. Totals stay exact because they are accumulated at read time, so the
reported size is right even though the lines behind it are gone.

One wrinkle the report does not mention: `headCap` is `max(outputThresholdLines,
sandwichHeadLines)` — **200, not 50**. The untruncated path returns *every* line, so
the head has to be able to hold a whole under-threshold output; sizing it to the
sandwich's 50 would have silently truncated every output between 51 and 200 lines.
`OutputAtTheThresholdEdge_IsStillWhole` pins that boundary.

**The byte ceiling** (`outputByteCeiling`, default 8 MB) is charged through a
`ByteBudget` shared by both streams — a runaway is a property of the command, not of
the pipe it happens to be writing to. On exhaustion the child is killed and the
result says so:

```
[Output limit reached at 8.0 MB - process killed. Narrow the command's output
 (grep/head/tail) and try again.]
```

`timedOut` stays a statement about the clock: the readers stop on either the timeout
or the ceiling (a linked CTS), but only the timeout sets the flag.

### The part that did not work first time

Cancelling *the collector* was not enough. With a runaway on stdout only, the stderr
reader stays parked on `ReadLineAsync` against a pipe that never reaches EOF while
the child lives, so `Task.WhenAll` waited out the full timeout and the ceiling bought
nothing — `RunawayProducer_HitsTheCeilingAndIsKilled` failed on `Assert.False(TimedOut)`
after **2 minutes**. The budget now exposes a `CancellationToken` that both readers
are linked to. Same test: **482 ms**.

### Measured

Peak RSS of the built binary, `yes hello | head -c N`, via `/usr/bin/time -v`:

| output read | before | after |
| --- | --- | --- |
| 7 MB (~1.2M lines) | 288 MB | 223 MB |
| 38 MB (~6.7M lines) | 517 MB | *n/a — ceiling stops the read at 8 MB* |
| 8 MB (ceiling hit) | — | 223 MB |

Before, peak scales with what the child emitted (288 → 517 MB as output goes 7 → 38
MB), which is the reported failure. After, it is flat at ~223 MB whether the command
emits 7 MB or is killed at the 8 MB ceiling.

**Being honest about that 223 MB:** it is not retention. It is allocation churn plus
runtime baseline — `ReadLineAsync` still allocates a `string` per line, and at ~1.2M
lines the GC heap grows to absorb the garbage even though nothing keeps it. This fix
bounds **retention**, not **allocation rate**. Bounding the latter means reading at
the char level instead of by line, which is a larger and much more invasive change;
it is not needed for the reported failure, since peak no longer tracks output size.

One residue worth naming rather than hiding: a single pathologically long *line* is
still materialised whole by `ReadLineAsync` before the collector ever sees it. Peak
is therefore O(longest line) rather than O(total output) — an enormous improvement on
the reported case, but not zero. A 50 MB line with no newline in it would still cost
50 MB.

### Tests

`nb.Tests/BashOutputBoundTests.cs`, 7 tests. The file says plainly which are which:
the ceiling tests assert genuinely new behaviour and were confirmed failing first;
the rest **assert the fix rather than reproduce the bug**, because the original repro
is a 20 MB/s runaway driven to `OutOfMemoryException` and is not reproducible at
proportionate cost.

The report's suggested `GC.GetTotalAllocatedBytes` proxy was tried and **discarded**:
it counts cumulative allocation including collected garbage, so it barely moves
between the two versions — a string is still allocated per line either way. What
separates them is *live* heap, which is why the evidence above is peak RSS of a real
run rather than a counter inside a test.

`LargeOutput_KeepsHeadAndTailAndCountsTheRest` is the one that actually exercises the
ring: its tail assertion covers lines that were enqueued and dropped ~100k times as
they streamed past.
