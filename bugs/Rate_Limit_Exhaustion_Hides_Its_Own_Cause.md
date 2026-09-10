---
kind: bug
title: A run killed by rate limiting reports neither the real cause nor the real attempt count
created: 2026-09-10
updated: 2026-09-10
status: current
state: open
severity: medium
cluster: provider-truthfulness
---

# A run killed by rate limiting reports neither the real cause nor the real attempt count

A 21-turn agentic run exited 3 / `rate_limited` after five minutes of retrying. Every
line nb printed about it was either incomplete or untrue, and diagnosing it took a
session with access to the proxy's journal — evidence nb had in hand and discarded.
Three defects, all in the same path.

## 1. The exhaustion message throws away the body that names the cause

`ConversationManager.cs`, in the `IsRateLimit` branch:

```csharp
AnsiConsole.MarkupLine($"...Rate limited; retries exhausted: {Markup.Escape(ex.Message)}...");
```

For System.ClientModel's `ClientResultException` that renders as:

```
Rate limited; retries exhausted: Service request failed.
Status: 429 (Too Many Requests)
```

The response body said `daily limit for '<upstream>' reached (150/day)` — a local
proxy's daily quota, cleared only at midnight, not a gateway throttle at all. That
string decided the whole diagnosis and never reached the log.

The infuriating part: `RateLimitClassifier` **already read it**. `TextOf()` calls
`ResponseBodyOf()` and classifies on message *plus* body, precisely because
`ClientResultException.Message` is uninformative — the class docs say so. The body is
then dropped on the floor at the one moment a human needs it.

Fix: have `IsRateLimit` hand back the text it classified on (it already computes it),
and print that instead of `ex.Message`.

## 2. `ParseRetryAfter` reads prose only, so a machine-readable reset is ignored

`ParseRetryAfter` scans text for `retry-after` / `try again in`. The rejection above
carried `X-RateLimit-Reset` on the response headers — same `GetRawResponse()` object
`ResponseBodyOf()` already reflects into — with a value hours away.

Had nb read it, `ShouldRetry` would have seen a hint exceeding the entire budget and
stopped in one attempt with an accurate reason. Instead it spent 300 seconds and 36
requests discovering nothing. The header comment ("the header is long gone by the time
an SDK exception reaches us") is not true for this SDK; the raw response is buffered
and attached.

Fix: read `Retry-After` and `X-RateLimit-Reset` off the raw response headers before
falling back to prose. A hint larger than the remaining budget should fail fast and say
so, rather than being clamped to `_maxDelay` and retried.

## 3. `PaceAsync` is charged to the retry budget, so the attempt counter lies

The run's last words were `attempt 8/10` — reading as two attempts of headroom left. The
attempt cap was never the binding constraint. `RetryBudgetSeconds` (300) was, and it was
consumed almost entirely by pacing rather than by backoff.

`PaceAsync()` runs inside the same `spent` stopwatch as the backoff, and `RaisePace()`
doubles the pace on every throttle up to `_maxDelay` (60 s). Measured gaps between the
nine request groups of the final call:

| gap | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | total |
|---|---|---|---|---|---|---|---|---|---|
| actual (s) | 4 | 8 | 17 | 31 | 60 | 59 | 60 | 61 | **300** |
| announced by nb (s) | 0.9 | 1.6 | 2.1 | 5.5 | 14.7 | 20.1 | 49.5 | 39.5 | 134 |

The pace ladder (4, 8, 16, 32, 60, 60, 60, 60) accounts for the actual gaps exactly.
The operator is told "retrying in 2.1s" and waits 17. `spent + delay <= _budget` then
refuses attempt 9 at 300 s, having performed 8 of a promised 10.

Two things are wrong here and they are separable:

- **The message under-reports the wait.** It should name the pace it is about to hold,
  not just the backoff delay: `retrying in 2.1s (pacing 16s) (attempt 3/10)`.
- **The pace arguably should not be charged to the retry budget at all.** The budget
  exists to bound "how long do we fight one throttle"; the pace is a steady-state drag
  that applies to unthrottled calls too. Charging it means the effective attempt count
  collapses as the pace rises — exactly when more attempts are wanted. If it stays
  charged, the exhaustion message must say the budget ran out, not imply the attempts
  did.

## Note on measurement

The gaps above were read from a proxy journal because nb's own stderr is untimestamped
and reports only its intended delays. All three defects share a root: nb knows more
about what happened than it says. See also
`Sdk_Retry_Policy_Multiplies_Every_Model_Call.md` — the request counts in that log are
4× what nb thinks it issued, which is a fourth way the same run was unreadable.
