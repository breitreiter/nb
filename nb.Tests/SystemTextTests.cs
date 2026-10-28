using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using nb.Harness;
using nb.MCP;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// §5.5: a program gets exactly the system directives it writes. nb's surface adds no
/// preamble, project instructions or environment block — the costumes that did retired
/// (plans/atif-and-acp.md stage 1). Captured off a recording <see cref="IChatClient"/>, so
/// what is asserted is what went on the wire.
/// </summary>
public class SystemTextTests
{
    [Theory]
    [InlineData("system Be terse.\nrun hello\n")]
    [InlineData("harness nb\nsystem Be terse.\nrun hello\n")]
    public async Task TheModelGetsOnlyTheSystemTextTheProgramWrote(string source)
    {
        var client = await RunProgram(source);

        var systems = client.CapturedMessages!.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Equal("Be terse.", Assert.Single(systems).Text);
    }

    // ---- harness ----

    private static async Task<RecordingChatClient> RunProgram(string source)
    {
        var client = new RecordingChatClient();
        await Evaluate(NewConversation(client), source);
        Assert.True(client.CapturedMessages is not null, "The model was never invoked.");
        return client;
    }

    private static ConversationManager NewConversation(IChatClient client, NbHarness? harness = null) =>
        new(client, new McpManager(), new FakeToolManager(), harness ?? new NbHarness(),
            new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false));

    private static Task Evaluate(ConversationManager conversation, string source)
    {
        var evaluator = new ProgramEvaluator(conversation, (_, _) => null, new List<string>());
        return evaluator.EvaluateAsync(ProgramParser.Parse(source));
    }

    /// <summary>Captures the messages of the first model call and ends the turn with plain text.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? CapturedMessages { get; private set; }

        private void Capture(IEnumerable<ChatMessage> messages) =>
            CapturedMessages ??= messages.ToList();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Capture(messages);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Capture(messages);
            await Task.CompletedTask;
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
