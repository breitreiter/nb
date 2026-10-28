using Microsoft.Extensions.Configuration;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The run journal (<c>plans/atif-and-acp.md</c> stage 2): the record is written as the run
/// happens, so it survives compaction, and it carries per-round metrics the wire omits.
/// </summary>
public class RunJournalTests
{
    // Compaction rewrites what the model sees. It used to rewrite the transcript too, because
    // the transcript was rebuilt from history after the run: a compacted run lost its early
    // turns and gained a "[Conversation summary]" message nobody sent.
    [Fact]
    public async Task Compaction_KeepsEveryTurnInTheTranscript()
    {
        var program = Nb.Program().System("be brief");
        for (int i = 0; i < 6; i++)
            program.User($"question {i}: " + new string('q', 200)).Assistant($"answer {i}: " + new string('a', 200));

        using var runtime = await NbRuntime.BuildAsync(MockConfig(maxContextTokens: 400), Options());
        await Evaluator(runtime).EvaluateAsync(program.Run("hello").Build());

        // Compaction ran: the model no longer sees the first question.
        Assert.DoesNotContain(runtime.Conversation.History, m => m.Text.StartsWith("question 0:"));

        var events = runtime.Conversation.Journal.Events;
        var users = events.OfType<UserEvent>().Select(u => u.Text).ToList();
        for (int i = 0; i < 6; i++)
            Assert.Contains(users, t => t!.StartsWith($"question {i}:"));
        Assert.DoesNotContain(events.OfType<AssistantTextEvent>(), a => a.Text.StartsWith("[Conversation summary]"));
    }

    [Fact]
    public async Task ModelRounds_CarryTheirMetrics_AndNothingElseDoes()
    {
        using var runtime = await NbRuntime.BuildAsync(MockConfig(), Options());
        await Evaluator(runtime).EvaluateAsync(Nb.Program().Add([new ApprovalEvent { Key = "bash", Value = "echo *" }]).Run("MOCK:tool=bash echo hi").Build());

        var entries = runtime.Conversation.Journal.Entries;
        Assert.All(entries.Where(e => e.Event is AssistantTextEvent or ToolCallEvent), e => Assert.NotNull(e.Round));
        Assert.All(entries.Where(e => e.Event is UserEvent or ToolResultEvent), e => Assert.Null(e.Round));

        var rounds = entries.Select(e => e.Round).OfType<RoundMetrics>().DistinctBy(r => r.Index).ToList();
        Assert.Equal([0, 1], rounds.Select(r => r.Index));
        Assert.All(rounds, r => Assert.True(r.Total > 0));
        Assert.Equal(entries.Select(e => e.At).Order(), entries.Select(e => e.At));
    }

    // A local model's inline <think> block is stripped from what the model is shown next;
    // it used to be dropped from the record too.
    [Fact]
    public async Task InlineThinkBlock_IsEmittedAsThinking_BeforeTheAnswer()
    {
        var result = await Nb.Program().Run("MOCK:response=<think>weigh it</think>hi").RunAsync(MockConfig(), Options());

        var thinking = Assert.Single(result.Events.OfType<ThinkingEvent>());
        var answer = Assert.Single(result.Events.OfType<AssistantTextEvent>());
        Assert.Equal("weigh it", thinking.Text);
        Assert.Equal("hi", answer.Text);
        Assert.Equal(answer.Turn, thinking.Turn);
        Assert.True(result.Events.ToList().IndexOf(thinking) < result.Events.ToList().IndexOf(answer));
    }

    private static ProgramEvaluator Evaluator(NbRuntime runtime) =>
        new(runtime.Conversation, runtime.ClientFactory, new List<string>(), runtime.DefaultHarnessFor, runtime.TemperatureFor);

    private static IConfiguration MockConfig(int? maxContextTokens = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["Trust"] = "false",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
            ["ChatProviders:0:MaxContextTokens"] = maxContextTokens?.ToString(),
        }).Build();

    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }
}
