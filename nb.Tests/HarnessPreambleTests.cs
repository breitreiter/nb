using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using nb.Harness;
using nb.MCP;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// A named harness brings its prompt. These assert the whole path — that the preamble
/// data file deploys, that the costume picks it up, and that it reaches the model as the
/// <b>first</b> system message, ahead of the program's own <c>system</c> directives.
///
/// Captured off a recording <see cref="IChatClient"/>, so what is asserted is what went
/// on the wire rather than what the evaluator believes it buffered.
/// </summary>
[Collection(ConsoleBoundCollection.Name)]
public class HarnessPreambleTests
{
    /// <summary>
    /// The preamble is a deployed <c>Content</c> file, not a string literal
    /// (rules/model-policy-in-prompt-layers.md clause 2), so "did it ship" is a real
    /// failure mode and gets its own assertion — the costume degrades quietly to
    /// tool-surface-only otherwise, and every test below would still pass on a
    /// costume that silently sent nothing.
    /// </summary>
    [Fact]
    public void QwenCode_PreambleFileIsDeployedAndLoaded()
    {
        var preamble = new QwenCodeHarness().Preamble;

        Assert.False(string.IsNullOrWhiteSpace(preamble),
            $"prompts/harness/qwen-code.md did not load from {AppContext.BaseDirectory}");
        Assert.Contains("interactive CLI agent", preamble);
    }

    /// <summary>The provenance header is for whoever reviews the file, not for the model.</summary>
    [Fact]
    public void Preamble_DoesNotShipItsProvenanceComment()
    {
        var preamble = new QwenCodeHarness().Preamble!;

        Assert.DoesNotContain("<!--", preamble);
        Assert.DoesNotContain("NOT VENDORED", preamble);
        Assert.StartsWith("You are", preamble);
    }

    [Fact]
    public void NbHarness_ContributesNoPreamble()
    {
        Assert.Null(new NbHarness().Preamble);
    }

    [Fact]
    public async Task NamedHarness_SendsPreambleAsTheFirstSystemMessage()
    {
        var client = await RunProgram("harness qwen-code\nsystem Be terse.\nrun hello\n");

        var messages = client.CapturedMessages!;
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Contains("interactive CLI agent", messages[0].Text);

        // The program gets the last word, which is how these harnesses layer project
        // context onto their own prompts.
        Assert.Equal(ChatRole.System, messages[1].Role);
        Assert.Equal("Be terse.", messages[1].Text);
    }

    /// <summary>
    /// §5.5 unchanged for anyone who names no harness: a program gets exactly the system
    /// directives it writes.
    /// </summary>
    [Fact]
    public async Task DefaultHarness_SendsNothingTheProgramDidNotWrite()
    {
        var client = await RunProgram("system Be terse.\nrun hello\n");

        var systems = client.CapturedMessages!.Where(m => m.Role == ChatRole.System).ToList();
        Assert.Equal("Be terse.", Assert.Single(systems).Text);
    }

    /// <summary>
    /// The directive is a config directive and may legally appear after a turn. The
    /// preamble still leads, and the flushed batch must stay turn-monotonic — authoring
    /// it at turn zero would throw here.
    /// </summary>
    [Fact]
    public async Task HarnessNamedAfterATurn_StillLeads()
    {
        var client = await RunProgram("system Be terse.\nharness qwen-code\nrun hello\n");

        var messages = client.CapturedMessages!;
        Assert.Contains("interactive CLI agent", messages[0].Text);
        Assert.Equal("Be terse.", messages[1].Text);
    }

    /// <summary>
    /// The preamble goes into history as an ordinary system message, so it lands in the
    /// transcript. That is what makes a <c>--seed</c> replay reproduce the run even if the
    /// costume has been edited since — the prompt is versioned by the run that used it.
    /// </summary>
    [Fact]
    public async Task Preamble_LandsInTheTranscriptAsAnOrdinarySystemEvent()
    {
        var conversation = NewConversation(new RecordingChatClient());
        await Evaluate(conversation, "harness qwen-code\nsystem Be terse.\nrun hello\n");

        var events = TranscriptMapper.FromHistory(conversation.History);
        var systems = events.OfType<SystemEvent>().ToList();

        Assert.Equal(2, systems.Count);
        Assert.Contains("interactive CLI agent", systems[0].Text);
        Assert.Equal("Be terse.", systems[1].Text);
    }

    /// <summary>
    /// The costume's context furniture reaches the wire too, after its prompt and before
    /// the program's own directives — the order the real harness uses, so the model is
    /// told what AGENTS.md is before it is handed one.
    /// </summary>
    [Fact]
    public async Task NamedHarness_SendsItsProjectInstructionsAfterItsPrompt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nb-test-agents-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "AGENTS.md"), "Prefer tabs in this repo.");
            var env = ShellEnvironment.Detect();
            env.SetCwd(dir);

            var client = new RecordingChatClient();
            var conversation = NewConversation(client,
                new NbHarness(new BashTool(env, defaultTimeoutSeconds: 120)));
            await Evaluate(conversation, "harness codex\nsystem Be terse.\nrun hello\n");

            var messages = client.CapturedMessages!;
            Assert.Contains("running in the Codex CLI", messages[0].Text);
            Assert.Contains("<INSTRUCTIONS>\nPrefer tabs in this repo.\n</INSTRUCTIONS>", messages[1].Text);
            Assert.Equal("Be terse.", messages[2].Text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>The prompt is a facsimile, and the costume says so rather than implying fidelity.</summary>
    [Fact]
    public void Costume_DeclaresThePreambleIsAFacsimile()
    {
        var omissions = new QwenCodeHarness().Omissions;

        var prompt = Assert.Single(omissions, o => o.StartsWith("system prompt:"));
        Assert.Contains("facsimile", prompt);
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
