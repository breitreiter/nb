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

        // Check for special mock instructions in the message
        var response = ParseMockInstruction(lastUserMessage) ?? _defaultResponse;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Just yield the full response as a single update
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

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
