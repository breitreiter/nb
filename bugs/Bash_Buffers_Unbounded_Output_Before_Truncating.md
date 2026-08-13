# `bash` buffers a command's entire output in memory, then throws away 99% of it

Status: Open (2026-08-13) — hit in a headless trial where a tool under test spun
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
