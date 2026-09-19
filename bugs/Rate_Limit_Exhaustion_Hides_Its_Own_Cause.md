---
kind: bug
title: A run killed by rate limiting reports neither the real cause nor the real attempt count
created: 2026-09-10
updated: 2026-09-19
status: current
state: fixed
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

## Fix (2026-09-19)

All three defects addressed, with defect 3 resolved **against this report's leaning** and
its reporting half dropped on the owner's instruction.

### 1. The exhaustion message names the cause

`RateLimitClassifier.IsRateLimit` has an overload handing back the text it classified on
(message plus response body), and `ConversationManager` prints that instead of
`ex.Message`. So the `daily limit for '<upstream>' reached (150/day)` that decided the
whole diagnosis now reaches the log instead of `Service request failed`.

### 2. Machine-readable resets are read

`ParseRetryAfterHeaders` reads `Retry-After` / `X-RateLimit-Reset` off the buffered raw
response (by reflection, same as `StatusOf`, since the SDK types are across the
`AssemblyLoadContext` boundary), handling both a numeric delta and an HTTP-date, and is
tried *before* the prose fallback. The report was right that the old comment — "the
header is long gone by the time an SDK exception reaches us" — is untrue for this SDK.

More importantly, **a hint longer than the remaining budget now fails fast** rather than
being clamped to `_maxDelay` and retried. That clamping is what let a run spend 300
seconds and 36 requests against a quota whose reset was hours away: the provider had
already said we could not win, and nb retried on a schedule the provider had explicitly
rejected.

### 3. Pacing is no longer charged to the retry budget

The report presented this as arguable. It is not, and the deciding argument is one the
report does not make: **nb is an eval harness, and a run truncated by a provider's pacing
produces a scoreable `rate_limited` transcript that reads as a fact about the model under
test.** A flaky provider should not be able to turn into a recorded result. That is the
same mis-attribution `provider` on the trailer was added to prevent.

The objection — that an uncharged budget leaves a call unbounded — does not hold:
`budget wall_ms` already bounds the run (`ExitReasons.TimeBudget`), and that ceiling is
the one the program author declared. The retry budget does not need to be a second,
implicit wall-clock limit.

`PaceAsync` now returns what it waited; the loop accumulates it and subtracts it before
the `ShouldRetry` budget check. The same number is charged to `provider_ms`
(`Trailer_Never_Carries_Duration.md`) — pacing is still time the run was blocked on a
provider, it is just not time spent fighting one throttle.

**The reporting half is dropped.** This report asked for `retrying in 2.1s (pacing 16s)`
and an exhaustion message naming the binding limit. Per the owner: consumers do not care
*why* a provider was slow, and nb should not break provider time into detailed findings.
The one bucket (`provider_ms`) plus the classified body from defect 1 is the whole
surface. A human who needs more has the body, which names the actual quota.

## A finding the report's model misses

Pacing does **not** generally eat the retry budget. `PaceAsync` measures the gap from the
*last request*, and a backoff has already elapsed since then — so while backoff >= pace,
pacing costs nothing extra and charging it was harmless. It only bites once the pace
outgrows the backoff, which is exactly the regime the measured run was in: its pace
pinned at the 60s cap while the announced backoff delays ran shorter. That is why the
report's table shows actual gaps of 4/8/17/31/60/59/60/61 against announced 0.9-49.5.

This matters for anyone reading the fix: the regression test has to set
`MinRequestIntervalMs` *above* `RetryMaxDelaySeconds` to discriminate at all. A first
attempt with the floor at or below the backoff cap passed against the unfixed code.

## Regression

`RetryBudgetAccountingTests` in `nb.Tests/RetryingChatClientTests.cs`:

- `PacingIsNotChargedToTheRetryBudget` — 2s floor against a 1s backoff cap and a 3s
  budget. **Observed red at 3 calls, green at 4.**
- `AHintLongerThanTheBudgetStopsImmediately` — an hour-long hint against a 30s budget
  stops after one call instead of retrying ten times.
- `ProviderTimeIsChargedEvenWithRetryDisabled` / `ProviderTimeIncludesBackoffAndPacing` —
  the measurement side.
