---
kind: bug
title: The OpenAI provider inherits the SDK's default retry policy, so every model call is four requests
created: 2026-09-10
updated: 2026-09-10
status: current
state: open
severity: high
cluster: provider-truthfulness
---

# The OpenAI provider inherits the SDK's default retry policy, so every model call is four requests

Found while diagnosing why `RetryingChatClient`'s backoff failed to save a 21-turn
agentic run through Cloudflare's AI Gateway (`openai/gpt-5.5`). The backoff was
working. The request count was four times what nb believed it was.

## The bug

`Providers/OpenAI/OpenAIProvider.cs`, `OpenAIOptions()`, sets `Endpoint` and
`Transport` and nothing else:

```csharp
var options = new OpenAIClientOptions();
if (!string.IsNullOrEmpty(endpoint)) options.Endpoint = new Uri(endpoint);
if (http is not null) options.Transport = new HttpClientPipelineTransport(http);
return options;
```

`ClientPipelineOptions` therefore installs System.ClientModel's default
`ClientRetryPolicy` — `maxRetries: 3`, retrying 408/429/5xx on its own ~0.8 s /
1.6 s / 3.2 s ladder. That policy lives inside the client pipeline, below
`AsIChatClient()` and below `DelegatingChatClient`, so it runs to completion before
`RetryingChatClient` is handed an exception. **nb never learns that three of every
four requests happened.**

`Providers/AzureOpenAI/AzureOpenAIProvider.cs` builds its client the same way and has
the same defect.

### Evidence

A reverse proxy sat between nb and the gateway, logging every request. Each nb-level
attempt appears as exactly four upstream requests. The final call of the run, nine
attempt groups, 36 requests:

```
05:57:12  POST .../chat/completions -> 429 (5ms)    <- nb attempt N
05:57:12  POST .../chat/completions -> 429 (2ms)    <- SDK retry 1
05:57:12  POST .../chat/completions -> 429 (2ms)    <- SDK retry 2
05:57:12  POST .../chat/completions -> 429 (2ms)    <- SDK retry 3
05:57:16  POST .../chat/completions -> 429 (5ms)    <- nb attempt N+1
```

36 rejections / 9 nb attempts = 4. Every group in the log is 4. Over the whole day
the two runs issued 186 requests to produce 45 completions; 104 of the 150 that
reached the gateway were 429s.

## Why it matters

Three harms, and none of them is "an extra request or two":

1. **It defeats the pacing nb just grew.** `MinRequestIntervalMs` (c3d1fcf) is a floor
   under the inter-request gap, and `PaceAsync` runs once per nb attempt. Set the floor
   to 5 s and, the moment anything throttles, nb still puts four requests on the wire
   inside ~5 s — a 48/min burst against a gateway measured near 12/min. The floor
   governs nb's attempts; the SDK governs the wire. They are not the same thing, and
   the config documents the former as if it were the latter.

2. **It spends someone's quota four times over.** Where a proxy or gateway meters
   requests rather than completions — and rejected requests are commonly metered — each
   nb attempt costs four units. The run above blew a 150-request daily cap on 45
   completions and then spent five minutes retrying against the cap it had just
   exhausted.

3. **It corrupts the retry budget.** The SDK's own ladder (~5.6 s) runs inside a single
   nb attempt, so `spent` in `ShouldRetry()` grows by time nb did not choose to spend
   and cannot attribute. See `Rate_Limit_Exhaustion_Hides_Its_Own_Cause.md`.

## The fix

One line in `OpenAIOptions()`, and the same in the Azure provider
(`System.ClientModel.Primitives` is already imported for the transport):

```csharp
options.RetryPolicy = new ClientRetryPolicy(maxRetries: 0);
```

Zero rather than one, because nb already owns this concern and owns it better: the
wall-clock budget, the half-jitter, the adaptive pace and its floor, and the
`rate_limited` exit reason all live in `RetryingChatClient`. Two retry layers stacked
do not compound resilience — they multiply request count and divide nb's visibility.
Leaving one SDK retry halves the amplification and keeps the invisible layer.

Every provider plugin that constructs a client from an SDK with a built-in retry
pipeline should be audited for the same thing; the defect is "we accepted the SDK's
defaults", not anything specific to OpenAI's.

## What the fix gives up

The SDK layer was also silently absorbing non-throttle transport faults — a dropped
connection, a bare 500. `RetryingChatClient` only retries what `RateLimitClassifier`
recognises (429/503/529 plus prose), so a socket reset that used to be retried three
times will now surface. If that resilience is wanted it belongs in `ShouldRetry()`,
where the budget and the pace can see it, not in a second pipeline underneath.

## Regression shape

A fake transport counting requests: one model call that always 429s should reach the
transport `MaxRetries + 1` times, not `4 × (MaxRetries + 1)`. Red before the fix at
`4×`.
