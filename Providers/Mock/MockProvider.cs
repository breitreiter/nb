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
        return new MockChatClient(response);
    }
}

/// <summary>
/// Mock chat client for testing. Supports MOCK:response=text in user messages
/// to control the response.
/// </summary>
public class MockChatClient : IChatClient
{
    private readonly string _defaultResponse;

    public MockChatClient(string defaultResponse = "OK")
    {
        _defaultResponse = defaultResponse;
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
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    // Maps a scripted tool name + raw arg to the argument dictionary that tool
    // expects. Only the tools exercised by tests need entries.
    private static Dictionary<string, object?> BuildToolArgs(string name, string arg) =>
        name.ToLowerInvariant() switch
        {
            "bash" => new() { ["command"] = arg, ["description"] = "scripted by MockProvider" },
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
