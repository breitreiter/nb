using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

public class MockProvider : IChatClientProvider
{
    public string Name => "Mock";
    public string[] RequiredConfigKeys => Array.Empty<string>();
    public bool CanCreate(IConfiguration config) => true;

    public IChatClient CreateClient(IConfiguration config)
    {
        var response = config["Response"] ?? "OK";
        var model = config["Model"];
        return new MockChatClient(response, model);
    }
}

/// <summary>
/// Mock chat client for testing. Supports MOCK:response=text in user messages
/// to control the response.
/// </summary>
public class MockChatClient : IChatClient
{
    // Fixed token usage reported per model round-trip, so tests can assert the
    // trailer's aggregate (e.g. a two-run program should report 2x these).
    public const int UsageInput = 10;
    public const int UsageOutput = 5;
    public const int UsageTotal = 15;

    private readonly string _defaultResponse;
    private readonly string? _model;
    private int _rateLimitHits;

    public MockChatClient(string defaultResponse = "OK", string? model = null)
    {
        _defaultResponse = defaultResponse;
        _model = model;
    }

    public ChatClientMetadata Metadata => new("MockProvider", new Uri("mock://localhost"), "mock-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Small delay to simulate real call
        await Task.Delay(10, cancellationToken);

        var lastUserMessage = chatMessages
            .LastOrDefault(m => m.Role == ChatRole.User)?
            .Text ?? "";

        // MOCK:throw simulates a mid-turn provider/model failure so the
        // exit-code contract's provider_error path (exit 2) is testable.
        if (lastUserMessage.StartsWith("MOCK:throw", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("mock provider failure");

        // MOCK:ratelimit simulates a throttling rejection, shaped like the gateway
        // rejections that have no usable HTTP status — only prose. Bare, it always
        // throws (retries get exhausted); MOCK:ratelimit=N throws N times and then
        // answers, so a successful retry is observable end-to-end.
        const string ratePrefix = "MOCK:ratelimit";
        if (lastUserMessage.StartsWith(ratePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var budget = lastUserMessage.Length > ratePrefix.Length && lastUserMessage[ratePrefix.Length] == '='
                && int.TryParse(lastUserMessage[(ratePrefix.Length + 1)..].Split(' ')[0], out var n) ? n : int.MaxValue;

            if (Interlocked.Increment(ref _rateLimitHits) <= budget)
                throw new InvalidOperationException(
                    "Wholesale rate limit exceeded for this gateway. Please reduce request rate or use BYOK.");

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
        }

        // MOCK:model echoes this client's configured model, so a mid-stream model
        // swap (which rebuilds the client) is observable end-to-end.
        if (lastUserMessage.StartsWith("MOCK:model", StringComparison.OrdinalIgnoreCase))
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _model ?? "(none)"));

        // MOCK:loop=<name> <arg> scripts an UNTERMINATING tool call: it re-emits the
        // same call every round (scanning ALL user turns, so an injected loop/todo
        // reminder can't derail it), so the doom-loop / token / tool-call rails are
        // the only things that stop it. The identical signature trips the detector.
        const string loopPrefix = "MOCK:loop=";
        var loopMsg = chatMessages.FirstOrDefault(m =>
            m.Role == ChatRole.User && (m.Text ?? "").StartsWith(loopPrefix, StringComparison.OrdinalIgnoreCase))?.Text;
        if (loopMsg != null)
        {
            var spec = loopMsg[loopPrefix.Length..];
            var parts = spec.Split(' ', 2);
            var call = new FunctionCallContent("mock-loop", parts[0], BuildToolArgs(parts[0], parts.Length > 1 ? parts[1] : ""));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
        }

        // MOCK:tool=<name> <arg> scripts a single tool call so approval/tool-loop
        // paths are testable. It fires once: as soon as a tool result is in
        // history, we fall through to a plain answer, so the turn terminates
        // after one round instead of re-emitting the call forever.
        const string toolPrefix = "MOCK:tool=";
        bool toolAlreadyRan = chatMessages.Any(m => m.Role == ChatRole.Tool);
        if (!toolAlreadyRan && lastUserMessage.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var spec = lastUserMessage[toolPrefix.Length..];
            var parts = spec.Split(' ', 2);
            var name = parts[0];
            var arg = parts.Length > 1 ? parts[1] : "";
            var call = new FunctionCallContent("mock-call-1", name, BuildToolArgs(name, arg));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
        }

        // Check for special mock instructions in the message
        var response = ParseMockInstruction(lastUserMessage) ?? _defaultResponse;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield the full response as a single update, carrying ALL content
        // (text and any function calls) so scripted tool calls survive the
        // streaming path — not just response.Text.
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);

        // MOCK:nousage / MOCK:partialusage reproduce what a proxy or gateway between nb
        // and the real provider does to the usage block — drops it entirely, or forwards
        // the parts without a total. Both are what the estimator fallback and the
        // total-from-parts derivation exist to survive.
        var lastUserMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (lastUserMessage.StartsWith("MOCK:nousage", StringComparison.OrdinalIgnoreCase))
            yield break;

        // A second update carrying usage, so ToChatResponse() aggregates it into
        // response.Usage the way a real streaming provider reports token counts.
        var usage = lastUserMessage.StartsWith("MOCK:partialusage", StringComparison.OrdinalIgnoreCase)
            ? new UsageDetails { InputTokenCount = UsageInput, OutputTokenCount = UsageOutput }
            : new UsageDetails { InputTokenCount = UsageInput, OutputTokenCount = UsageOutput, TotalTokenCount = UsageTotal };
        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    // Maps a scripted tool name + raw arg to the argument dictionary that tool
    // expects. Only the tools exercised by tests need entries.
    private static Dictionary<string, object?> BuildToolArgs(string name, string arg) =>
        name.ToLowerInvariant() switch
        {
            "bash" => new() { ["command"] = arg, ["description"] = "scripted by MockProvider" },
            "search_web" => new() { ["query"] = arg },
            "fetch_url" => new() { ["url"] = arg },
            // Content is fixed: these scripted calls exercise approval and the tool loop,
            // and no test so far has cared what got written — only whether it was allowed.
            "write_file" => new() { ["path"] = arg, ["content"] = "scripted by MockProvider\n" },
            // read_file keeps its name across the qwen-code costume but changes its
            // parameter spelling, and the mock cannot see which harness is active — so
            // send both; the unused one is ignored either way.
            "read_file" => new() { ["path"] = arg, ["file_path"] = arg },
            "list_dir" => new() { ["path"] = arg },

            // The qwen-code costume's spellings, so a scripted call exercises the
            // harness's inbound translation end to end (plans/harness-emulation.md).
            "run_shell_command" => new() { ["command"] = arg, ["is_background"] = false, ["timeout"] = 30000 },
            "list_directory" => new() { ["file_path"] = arg, ["path"] = arg },
            "glob" => new() { ["pattern"] = arg },
            "grep_search" => new() { ["pattern"] = arg, ["glob"] = "*", ["limit"] = 5 },

            _ => new() { ["input"] = arg },
        };

    private static string? ParseMockInstruction(string message)
    {
        // Support MOCK:response=<text> to specify exact response
        const string prefix = "MOCK:response=";
        if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return message[prefix.Length..];
        }
        return null;
    }
}
