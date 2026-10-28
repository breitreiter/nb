using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// <c>--trajectory</c>'s ATIF (<c>plans/atif-and-acp.md</c> stage 3). Structural rules from
/// Harbor's v1.7 models, which reject a violation outright (<c>extra="forbid"</c>), plus the
/// fields only the journal can fill. The opt-in eval runs Harbor's own validator.
/// </summary>
public class AtifWriterTests
{
    private static readonly HashSet<string> TopKeys = ["schema_version", "agent", "steps", "final_metrics", "extra"];
    private static readonly HashSet<string> StepKeys =
        ["step_id", "timestamp", "source", "message", "model_name", "reasoning_content", "tool_calls", "observation", "metrics", "llm_call_count", "extra"];
    private static readonly HashSet<string> AgentOnly = ["model_name", "reasoning_content", "tool_calls", "metrics", "llm_call_count"];

    // nb's documented `extra` keys, by where they sit. Anything else is an undocumented field.
    private static readonly HashSet<string> StepExtra = ["source", "keys", "assistant_json"];
    private static readonly HashSet<string> CallExtra = ["approved", "approval_reason"];
    private static readonly HashSet<string> ResultExtra = ["result"];
    private static readonly HashSet<string> MetricsExtra = ["estimated"];
    private static readonly HashSet<string> TopExtra = ["result"];

    [Theory]
    [InlineData("MOCK:tool=bash echo hi")]
    [InlineData("MOCK:tool=bash echo changed > note.txt")]
    [InlineData("MOCK:loop=bash echo hi")]
    [InlineData("hello")]
    public async Task MockRuns_AreWellFormed(string prompt)
    {
        var result = await Nb.Program()
            .Add([new ApprovalEvent { Key = "bash", Value = "echo hi" }])
            .Budget("tool_calls", 8)
            .Run(prompt)
            .RunAsync(MockConfig(), Options());

        AssertWellFormed(result.Trajectory());
    }

    [Fact]
    public async Task Steps_MirrorTheTurns_AndCarryTheJournalsMetrics()
    {
        var result = await Nb.Program()
            .Add([new ApprovalEvent { Key = "bash", Value = "echo hi" }])
            .Run("MOCK:tool=bash echo hi")
            .RunAsync(MockConfig(), Options());
        var t = result.Trajectory();
        var steps = t["steps"]!.AsArray().Select(s => s!.AsObject()).ToList();

        Assert.Equal(["user", "agent", "agent"], steps.Select(s => (string)s["source"]!));
        var call = steps[1]["tool_calls"]![0]!;
        Assert.Equal("bash", (string)call["function_name"]!);
        Assert.Equal("allow", (string)call["extra"]!["approved"]!);
        Assert.Equal((string)call["tool_call_id"]!, (string)steps[1]["observation"]!["results"]![0]!["source_call_id"]!);
        Assert.Equal("OK", (string)steps[2]["message"]!);

        Assert.All(steps.Skip(1), s =>
        {
            Assert.Equal(1, (int)s["llm_call_count"]!);
            Assert.Equal("mock-model", (string)s["model_name"]!);
            Assert.True((long)s["metrics"]!["prompt_tokens"]! > 0);
        });
        Assert.Equal(steps.Count, (int)t["final_metrics"]!["total_steps"]!);
        Assert.Equal("nb", (string)t["agent"]!["name"]!);
        Assert.DoesNotContain("+", (string)t["agent"]!["version"]!);
        Assert.Equal("ok", (string)t["extra"]!["result"]!["exit_reason"]!);
    }

    [Fact]
    public void EveryEventKind_MapsAsProctorsReaderDid()
    {
        var journal = new RunJournal();
        var round = new RoundMetrics(0, 100, 20, 120, Estimated: true, "m", CostUsd: 0.25);
        journal.Append(new ChatMessage(ChatRole.System, "be terse"));
        journal.Append(new ChatMessage(ChatRole.User, [new TextContent("look"), new DataContent(new byte[] { 1 }, "image/png")]));
        journal.Append(new ChatMessage(ChatRole.Assistant, "plan\n```json\n{\"a\":1}\n```"), round, thinking: "weigh it");
        journal.Append(new ChatMessage(ChatRole.User, "prod"), oracle: new OracleAnswer(["env"], "env"));
        journal.Append(new ChatMessage(ChatRole.Assistant, "nb's own note"));

        var t = AtifWriter.Write(journal.Entries, TranscriptMapper.ResultTrailer(journal.Events, nbVersion: "1.2.3+abc", model: "m"));
        AssertWellFormed(t);
        var steps = t["steps"]!.AsArray().Select(s => s!.AsObject()).ToList();

        Assert.Equal(["system", "user", "agent", "user", "agent"], steps.Select(s => (string)s["source"]!));
        Assert.Equal("look\n[image]", (string)steps[1]["message"]!);
        Assert.Equal("weigh it", (string)steps[2]["reasoning_content"]!);
        Assert.Equal(1, (int)steps[2]["extra"]!["assistant_json"]!["a"]!);
        Assert.Equal(0.25, (double)steps[2]["metrics"]!["cost_usd"]!);
        Assert.True((bool)steps[2]["metrics"]!["extra"]!["estimated"]!);
        Assert.Equal("oracle", (string)steps[3]["extra"]!["source"]!);
        Assert.Equal(["env"], steps[3]["extra"]!["keys"]!.AsArray().Select(k => (string)k!));

        // A message nb wrote, not a model: no model call, so nothing to measure. Omitted, not 0:
        // 0 would claim a deterministic dispatch, which a fabricated-history turn is not either.
        Assert.Null(steps[4]["llm_call_count"]);
        Assert.Null(steps[4]["metrics"]);
        Assert.Equal("1.2.3", (string)t["agent"]!["version"]!);
    }

    // Directives configure a run and are never journaled; the trailer goes under extra.result.
    private static readonly HashSet<string> NotJournaled =
        ["run", "provider", "model", "harness", "mcp", "tools", "approval", "loop", "budget", "oracle", "result"];

    // The writer throws on an event type it does not map, so a new conversation event needs a
    // mapping before any --trajectory run can emit it.
    [Fact]
    public void EveryConversationEventType_HasAnAtifMapping()
    {
        var events = typeof(TranscriptEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(TranscriptEvent)) && !t.IsAbstract)
            .Select(t => (TranscriptEvent)RuntimeHelpers.GetUninitializedObject(t))
            .Where(e => !NotJournaled.Contains(e.Type))
            .ToList();
        Assert.NotEmpty(events);

        foreach (var e in events)
        {
            var entry = new JournalEntry(e, DateTimeOffset.UtcNow, null);
            try { AtifWriter.Write([entry], new ResultEvent { ExitReason = "ok" }); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("no ATIF mapping")) { Assert.Fail(ex.Message); }
            catch (Exception) { } // an uninitialized event may not serialize; only a missing mapping fails here
        }
    }

    private static void AssertWellFormed(JsonObject t)
    {
        Assert.Equal(AtifWriter.SchemaVersion, (string)t["schema_version"]!);
        Assert.Subset(TopKeys, t.Select(p => p.Key).ToHashSet());
        Assert.Subset(TopExtra, Keys(t["extra"]));
        Assert.Subset(MetricsExtra, Keys(t["final_metrics"]?["extra"]));

        var steps = t["steps"]!.AsArray();
        Assert.NotEmpty(steps);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!.AsObject();
            var keys = step.Select(p => p.Key).ToHashSet();
            Assert.Equal(i + 1, (int)step["step_id"]!);
            Assert.Subset(StepKeys, keys);
            Assert.True(DateTimeOffset.TryParse((string)step["timestamp"]!, out _));
            Assert.Contains((string)step["source"]!, new[] { "system", "user", "agent" });
            Assert.IsType<string>(step["message"]!.GetValue<object>());
            if ((string)step["source"]! != "agent")
                Assert.Empty(keys.Intersect(AgentOnly));
            Assert.Subset(StepExtra, Keys(step["extra"]));
            Assert.Subset(MetricsExtra, Keys(step["metrics"]?["extra"]));

            var callIds = step["tool_calls"]?.AsArray().Select(c => (string)c!["tool_call_id"]!).ToHashSet() ?? [];
            foreach (var call in step["tool_calls"]?.AsArray() ?? [])
                Assert.Subset(CallExtra, Keys(call!["extra"]));
            foreach (var r in step["observation"]?["results"]?.AsArray() ?? [])
            {
                Assert.Contains((string)r!["source_call_id"]!, callIds);
                Assert.Subset(ResultExtra, Keys(r["extra"]));
            }
        }
    }

    private static HashSet<string> Keys(JsonNode? o) => o is JsonObject obj ? obj.Select(p => p.Key).ToHashSet() : [];

    private static IConfiguration MockConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["Trust"] = "false",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
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
