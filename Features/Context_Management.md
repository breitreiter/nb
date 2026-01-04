# Context Management

Status: Research

## Problem

Conversations grow unbounded. When the context window is exceeded, the API call fails with a provider-specific error. Currently nb has no proactive management—history just accumulates in `_conversationHistory` and persists to `.nb_conversation_history.json`.

## Challenge: Unknown Context Limits

nb supports multiple providers (OpenAI, Anthropic, Azure, Gemini) via a plugin architecture. Each provider can be configured with different models having vastly different context windows:

| Model | Context Window |
|-------|---------------|
| gpt-4o | 128k |
| gpt-4 | 8k / 32k |
| gpt-4-turbo | 128k |
| claude-3.5-sonnet | 200k |
| claude-3-opus | 200k |
| gemini-1.5-pro | 1M+ |

The current `IChatClientProvider` interface has no mechanism to report context window size:

```csharp
public interface IChatClientProvider
{
    string Name { get; }
    string[] RequiredConfigKeys { get; }
    IChatClient CreateClient(IConfiguration config);
    bool CanCreate(IConfiguration config);
}
```

Model selection happens in provider config (`"Model": "gpt-4o-mini"`), so even the same provider can have wildly different limits.

## Options Considered

### Option 1: Config-Driven (Recommended)

Add optional `ContextWindowSize` to provider configuration:

```json
{
  "Name": "OpenAI",
  "ApiKey": "...",
  "Model": "gpt-4o",
  "ContextWindowSize": 128000
}
```

**Pros:**
- Simple, no interface changes
- User has explicit control
- Works with any model, including new/custom ones

**Cons:**
- Manual—user must know and set the value
- Can get out of sync if model changes

**Default behavior:** Use generous default (128k?) when not specified.

### Option 2: Extend Provider Interface

Add method to `IChatClientProvider`:

```csharp
int? GetContextWindowSize(IConfiguration config);
```

Providers implement via hardcoded lookup table or config read.

**Pros:**
- Provider encapsulates the knowledge
- Could include model→size mappings

**Cons:**
- Lookup tables get stale as new models release
- Still needs config fallback for unknown models
- Interface change requires updating all providers

### Option 3: Runtime Metadata Query

Microsoft.Extensions.AI has `IChatClient.GetMetadataAsync()`. Could query at startup.

**Pros:**
- Dynamic, always current

**Cons:**
- Not all providers/SDKs expose this
- Extra API call at startup
- Metadata schema isn't standardized

### Option 4: Reactive Only

Don't track proactively. Catch context overflow errors and handle gracefully.

**Pros:**
- Zero configuration
- Works with any provider

**Cons:**
- Poor UX—user loses their message when it fails
- Different providers return different errors (hard to detect reliably)

## Recommended Approach

Hybrid of **Option 1** (config override) + **Option 2** (lookup table) + **Option 4** (reactive fallback).

### Context Window Resolution Order

1. **Explicit config** - `ContextWindowSize` in provider config (user override)
2. **Model lookup file** - `models.json` alongside executable
3. **Embedded fallback** - Compiled-in defaults for common models (safety net if file missing)
4. **Conservative default** - 32k for truly unknown models + startup warning

### Model Lookup File

Ship `models.json` with known context windows. Use prefixes to cover model families:

```json
{
  "gpt-4o": 128000,
  "gpt-4-turbo": 128000,
  "gpt-4-32k": 32768,
  "gpt-4": 8192,
  "gpt-3.5-turbo": 16385,
  "claude-3": 200000,
  "gemini-1.5": 1000000,
  "gemini-2": 1000000
}
```

**Location:** Same directory as executable, alongside `appsettings.json`.

**Partial/Prefix Matching:** To avoid maintaining entries for every dated model variant (e.g., `gpt-4o-2024-08-06`, `gpt-4o-2024-11-20`), support prefix matching:

```json
{
  "gpt-4o": 128000,
  "gpt-4": 8192,
  "claude-3": 200000,
  "gemini-1.5": 1000000
}
```

Lookup strategy:
1. Exact match first (`gpt-4o-2024-08-06`)
2. If no exact match, try prefix match (`gpt-4o` matches `gpt-4o-2024-08-06`)
3. Longest prefix wins (`gpt-4` vs `gpt-4o` for `gpt-4o-mini` → `gpt-4o` wins)

This keeps the file small while covering model families.

**Benefits:**
- Update without recompiling (add new models as they release)
- Users can add custom/private/fine-tuned models
- Simple format, easy to maintain

**Embedded fallback:** Compile the same data into the binary. If `models.json` is missing or malformed, fall back to embedded defaults. App should never break due to missing file.

### Proactive Warning

Track approximate token usage during conversation. Warn user when approaching limit (80% threshold). Let user decide: continue, clear history, or summarize.

### Reactive Safety Net

Catch context overflow errors from providers. Offer to clear oldest messages and retry. Different providers return different errors—need to pattern-match common ones.

## Research: Context Degradation

Research confirms that LLM performance degrades as context fills, but the pattern is more complex than a simple threshold.

### Key Findings

**"Lost in the Middle" phenomenon** (Liu et al., 2024): Models show a U-shaped performance curve—they retrieve information well from the beginning and end of context but struggle with middle-positioned content. This is attributed to causal attention favoring early tokens and RoPE positional encoding decaying attention to distant tokens.

**Degradation starts earlier than advertised:**
- NoLiMa benchmark: 11/12 models dropped below 50% performance at 32k tokens
- Models claiming 200k often become unreliable around 130k
- Databricks found Llama-3.1-405b degrades after 32k, GPT-4-0125 after 64k

**Chroma "Context Rot" study** (2024): Tested 18 models, all exhibited consistent performance decline with increased context. Key observations:
- Lower needle-question similarity = more pronounced degradation
- Even a single distractor document reduces performance
- Claude models: conservative, abstain when uncertain, lowest hallucination
- GPT models: higher hallucination rates with distractors
- Gemini models: can produce random text at long contexts

### Implications for nb

- Warning threshold should be earlier than originally thought (50% not 80%)
- Position matters as much as volume—important info in the middle is vulnerable
- Different providers degrade differently; can't assume uniform behavior

### Sources

- [Lost in the Middle - ACL 2024](https://aclanthology.org/2024.tacl-1.9/)
- [Context Rot - Chroma Research](https://research.trychroma.com/context-rot)
- [Long Context RAG Performance - Databricks](https://www.databricks.com/blog/long-context-rag-performance-llms)

## Token Counting

### Use Provider-Reported Usage (Preferred)

Microsoft.Extensions.AI exposes actual token counts from the provider:

```csharp
ChatResponse response = await client.GetResponseAsync(messages, options);

int? inputTokens = response.Usage?.InputTokenCount;   // context size sent
int? outputTokens = response.Usage?.OutputTokenCount; // response size
int? totalTokens = response.Usage?.TotalTokenCount;
```

**This is ground truth** — no estimation needed. Track `InputTokenCount` after each response to know exactly how much context we've used.

### Hybrid Approach for Proactive Warning

We only get usage *after* the API call, so we can't prevent overflow — only react. For proactive warning before sending:

1. Track `lastInputTokenCount` from previous response
2. Estimate new user message: `newMessageEstimate = message.Length / 3` (conservative)
3. Warn if `lastInputTokenCount + newMessageEstimate > threshold`

This gives us ground truth for historical usage and only estimates the delta (the new message). Much more accurate than estimating everything.

### Fallback: Character-Based Estimation

If `response.Usage` is null (provider doesn't support it), fall back to conservative estimation:

```csharp
int EstimateTokens(string text) => text.Length / 3;
```

The 4-char rule (~4 chars/token) is commonly cited but can be 30-40% off for code, emojis, or non-English text. Using 3 chars/token overestimates, which is safer for warnings.

### Sources

- [Tracking Token Usage with Microsoft.Extensions.AI](https://markheath.net/post/2025/1/11/tracking-token-usage-microsoft-extensions-ai)
- [UsageDetails - Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.usagedetails.totaltokencount)

## Overflow Handling Strategies

When context is full or nearly full:

### Strategy A: Sliding Window
Drop oldest messages (keep system prompt + recent N messages).

**Pros:** Simple, preserves recent context
**Cons:** Loses potentially important early context

### Strategy B: Summarization
Ask the model to summarize old messages, replace them with summary.

**Pros:** Preserves key information
**Cons:** Extra API call, summary quality varies, recursive problem (summary uses tokens too)

### Strategy C: User Choice
Warn user, let them decide: clear history, summarize, or continue anyway.

**Pros:** User control
**Cons:** Interrupts flow

### Recommendation

Start with **Strategy C** (user choice) for the warning case, **Strategy A** (sliding window) for the reactive/error case.

## UX Considerations

**Problem:** Console real estate is precious. Every system message, warning, or status indicator steals pixels from actual conversation content. The interface already has noise from tool approvals, spinner, etc.

**Tension:** We want users aware of context usage, but inline warnings per-turn add clutter. Need to think carefully about where/how to surface this.

**Options to explore:**
- Status bar/footer (persistent but minimal)
- Only warn at thresholds (50%? 75%?) not every turn
- Color-coded prompt indicator (green → yellow → red)
- On-demand via command (`/status` or `?`)
- Warn once per session at threshold, not repeatedly

**Non-goals:**
- Per-message token counts (too noisy)
- Verbose warnings that interrupt flow

Needs more thought. The right UX probably depends on how often users actually hit context limits in practice.

## Open Questions

1. ~~What's a reasonable default context window size?~~ → 32k for unknown models
2. ~~Warning threshold~~ → 50% based on research (not 80%)
3. ~~Token counting approach~~ → Use `response.Usage.InputTokenCount` from provider, estimate delta only
4. ~~Interactive vs single-shot mode~~ → Same auto-cull behavior, just omit UI warnings in single-shot
5. ~~Should `/clear` offer to summarize?~~ → No
6. What error patterns indicate context overflow for each provider?
7. ~~Partial model name matching~~ → Yes, use prefix/pattern matching to keep `models.json` manageable
8. **UX: How to show context usage without adding noise?**
