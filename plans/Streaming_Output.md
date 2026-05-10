# nb Streaming Output Support

Status: Proposed

## Overview

Add streaming response support to nb so the model's output appears incrementally as it's generated, rather than waiting for the complete response. This provides immediate feedback when models are processing long responses and helps diagnose hanging or slow prompts.

## Problem Statement

Current implementation uses `CompleteAsync()` which blocks until the entire response is generated. This creates several issues:

1. **No feedback during generation** - User can't tell if the model is thinking, generating, or hung
2. **Perceived latency** - Long responses feel slower when you wait for completion vs seeing incremental progress
3. **Debugging difficulty** - When a model hangs on a specific prompt, you can't see partial output to understand what triggered the hang
4. **Poor UX for long responses** - Multi-paragraph explanations or code listings feel unresponsive

## Microsoft.Extensions.AI Streaming Support

The `IChatClient` interface provides streaming via `CompleteStreamingAsync()`:

```csharp
// Current non-streaming approach
ChatCompletion response = await chatClient.CompleteAsync(messages, options);

// Streaming approach
IAsyncEnumerable<StreamingChatCompletionUpdate> stream =
    chatClient.CompleteStreamingAsync(messages, options);

await foreach (var update in stream)
{
    // Process incremental update
    if (update.Text != null)
    {
        // Render text chunk
    }

    if (update.FinishReason != null)
    {
        // Response complete
    }
}
```

Key differences:
- Returns `IAsyncEnumerable<StreamingChatCompletionUpdate>` instead of `ChatCompletion`
- Updates arrive as chunks with incremental text
- Tool calls arrive incrementally (name first, then argument chunks)
- Final update includes `FinishReason` to signal completion

## UI Rendering Strategy

### Text Streaming with Spectre.Console

Spectre.Console's `Live` display is ideal for streaming output:

```csharp
await AnsiConsole.Live(new Markup(""))
    .StartAsync(async ctx =>
    {
        var buffer = new StringBuilder();

        await foreach (var update in stream)
        {
            if (update.Text != null)
            {
                buffer.Append(update.Text);
                ctx.UpdateTarget(new Markup(Markup.Escape(buffer.ToString())));
            }
        }
    });
```

Considerations:
- **Markup escaping** - User-facing text must escape Spectre.Console markup characters (`[`, `]`)
- **Partial markdown** - Streaming markdown can produce invalid markup mid-stream (e.g., opening `**` without closing)
- **Line buffering** - Consider buffering partial lines to avoid rendering artifacts
- **Cursor positioning** - Live display handles this automatically

### Progress Indication

Show thinking/generation state before first chunk arrives:

```
[dim]◐ Thinking...[/]

→ Once first chunk arrives, replace with actual content
```

For long pauses mid-stream (tool calls, processing), show status:

```
The current directory contains 15 files...

[dim]◐ Running tool: run_cmd...[/]

Here are the results...
```

## Tool Calls in Streaming

Tool calls complicate streaming significantly. The model must:
1. Generate tool call (name + arguments stream in chunks)
2. Wait for tool execution (nb runs the tool)
3. Resume generation with tool results

### Two Streaming Strategies

**Option A: Hybrid Streaming (Recommended)**

Stream text content, but pause streaming for tool calls:

```
[streaming text appears incrementally...]

The files in this directory are:

[pause streaming, show tool UI]
run: ls -la
[Y]es [N]o [A]lways [?]

[execute tool, resume streaming]

total 48
drwxr-xr-x  12 user  staff   384 Jan 12 10:30 .
drwxr-xr-x   8 user  staff   256 Jan 11 15:22 ..
...

[streaming continues...]
```

Process:
1. Stream text chunks as they arrive
2. When tool call starts, accumulate chunks until complete
3. Pause stream, show approval UI, execute tool
4. Resume streaming with tool result in context

**Option B: Full Streaming (Complex)**

Continue streaming while accumulating tool calls:

```
[streaming continues during tool call...]

Let me check the files:

[tool call appears as structured aside while text streams]
┌─ Tool Call: run_cmd ─────────────────┐
│ Waiting for approval...              │
│ [Y]es [N]o [A]lways [?]              │
└──────────────────────────────────────┘

[text continues streaming around the tool UI...]
```

This is complex because:
- Text and tool calls arrive interleaved
- UI must multiplex tool approval prompts with streaming text
- Spectre.Console `Live` display would need careful layout management

**Recommendation:** Start with Option A (hybrid). Streaming text is the 90% case and provides most of the UX benefit. Tool calls are infrequent enough that pausing is acceptable.

## Tool Call Streaming Details

Tool calls stream in multiple chunks:

```csharp
await foreach (var update in stream)
{
    foreach (var toolCall in update.Contents.OfType<FunctionCallContent>())
    {
        // First update: toolCall.Name is set, Arguments may be partial
        // Subsequent updates: Arguments accumulate
        // Final update: Complete JSON in Arguments
    }
}
```

Challenges:
- **Partial JSON** - Arguments arrive as incomplete JSON strings
- **Multiple tool calls** - Model can request multiple tools in one response
- **Validation** - Can't validate/execute until JSON is complete

Implementation approach:
1. Accumulate tool call chunks in a buffer keyed by CallId
2. When `FinishReason == ChatFinishReason.ToolCalls`, all tool calls are complete
3. Pause streaming, show approval UI(s), execute tools
4. Make new streaming request with tool results

## Single-Shot Mode Consideration

Single-shot mode (`./nb "prompt"`) should respect streaming for consistency:

```bash
./nb "explain this code" < file.py
# Streams output to terminal, still exits when complete
```

Benefit: User sees progress even in single-shot mode, making slow prompts less frustrating.

## Implementation Phases

### Phase 1: Basic Text Streaming

- Implement `CompleteStreamingAsync` flow in `ConversationManager`
- Render streaming text with `Spectre.Console.Live`
- Handle finish reasons (Stop, Length)
- No tool call support yet - fall back to non-streaming if tools are enabled

Delivers immediate value for text-only interactions.

### Phase 2: Hybrid Tool Streaming

- Detect tool calls in stream
- Accumulate tool call chunks until complete
- Pause stream, execute tools with existing approval UI
- Resume streaming with tool results

Enables streaming for the full feature set.

### Phase 3: Polish

- Add "thinking" spinner before first chunk
- Show tool execution status during pause
- Handle edge cases (empty responses, immediate tool calls, etc.)
- Optimize buffering strategy for smooth rendering

## Provider Compatibility

All major providers support streaming:
- **Azure OpenAI** - Native streaming support via SDK
- **Anthropic** - Streaming available in Claude API
- **OpenAI** - Full streaming support
- **Gemini** - Supports streaming responses

Microsoft.Extensions.AI abstracts this, so provider-specific details are hidden behind `IChatClient.CompleteStreamingAsync()`.

**Mock Provider** - Should implement fake streaming (emit chunks with small delays) for testing.

## Configuration

Add streaming controls to appsettings:

```json
{
  "streaming": {
    "enabled": true,
    "chunkBufferLines": 1,
    "showThinkingIndicator": true,
    "fallbackToNonStreaming": false
  }
}
```

- `enabled` - Master toggle (default: true)
- `chunkBufferLines` - Buffer N lines before rendering (reduces flickering)
- `showThinkingIndicator` - Show spinner before first chunk
- `fallbackToNonStreaming` - If streaming fails, retry with non-streaming

## Error Handling

Streaming introduces new failure modes:

1. **Stream disconnection** - Network drops mid-stream
2. **Partial JSON** - Stream ends with incomplete tool call
3. **Rate limits** - Provider throttles mid-stream

Strategy:
- Wrap streaming in try-catch
- On error, show what was received so far
- Offer retry with non-streaming mode
- Log partial response for debugging

```
The current directory contains 15 files, including:
- Program.cs
- nb.csproj

[Connection lost during streaming]

Received partial response (157 tokens).
[R]etry with streaming  [r]etry without streaming  [c]ancel
```

## Open Questions

1. **Line buffering** - Should we buffer partial lines to avoid rendering mid-word, or stream truly incrementally? Buffering trades latency for smoothness.

2. **Tool call feedback** - When streaming pauses for tool calls, should we show the partial text so far, or hide it until tool completes? Showing it provides context but might be confusing.

3. **Multi-turn streaming** - For multi-turn tool calls (model calls tool, gets result, calls another tool), should each turn stream independently or treat the whole sequence as one streaming session?

4. **History management** - Should conversation history store the final completed message, or preserve the chunked nature for future replay?

## References

- [Microsoft.Extensions.AI Streaming Documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai#streaming-responses)
- [Spectre.Console Live Display](https://spectreconsole.net/live/live-display)
- [Azure OpenAI Streaming](https://learn.microsoft.com/en-us/azure/ai-services/openai/how-to/streaming)
