using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using nb.Harness;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The tripwire for what proctor (<c>../proctor</c>) reads from nb strictly
/// (<c>plans/atif-and-acp.md</c>, stage 0). Each list below has a twin in proctor, named
/// beside it. A red test here means the change has a proctor side: make that change too,
/// in the same stage, and update the list with it. Updating the list alone is exactly the
/// forgetting this exists to stop.
///
/// Only what proctor is <i>strict</i> about is pinned. Its JSONL reader ignores unknown
/// fields and event types, and since stage 3 it reads nb's own ATIF, whose event mapping nb
/// owns (<c>AtifWriterTests.EveryConversationEventType_HasAnAtifMapping</c>). A new tool
/// name is still a proctor change.
/// </summary>
public class ProctorContractTests
{
    /// <summary>
    /// <c>Run/Ingest.cs</c> <c>Kinds[Arm.Nb]</c>: every tool name proctor can give a kind.
    /// Any other name is <c>adapter_unmapped</c>. MCP, fake and <c>nb_*_resources</c> tools are
    /// out of scope: no proctor suite declares them, and their names are the suite's, not nb's.
    /// </summary>
    private static readonly HashSet<string> ProctorToolNames =
    [
        "bash", "read_file", "list_dir", "write_file", "edit_file", "apply_patch",
        "find_files", "grep", "fetch_url", "search_web", "todo_write", "todo_read",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryNbSurfaceTool_IsOneProctorKinds(bool applyPatchStyle)
    {
        var env = ShellEnvironment.Detect();
        var harness = new NbHarness(
            new BashTool(env, defaultTimeoutSeconds: 120), new ReadFileTool(env), new WriteFileTool(env), new EditFileTool(env),
            new FindFilesTool(env), new GrepTool(env), new ListDirTool(env), new FetchUrlTool(),
            SearchWebTool.FromConfig(null, null), new ApplyPatchTool(env), applyPatchStyle);

        var unkinded = harness.CreateTools(ToolSurface.All).Select(t => t.Name).Except(ProctorToolNames).Order().ToList();
        Assert.True(unkinded.Count == 0,
            $"nb advertises tool(s) proctor's ingest has no kind for: {string.Join(", ", unkinded)}. " +
            "Add them to Kinds[Arm.Nb] in ../proctor/Run/Ingest.cs first, then here.");
    }

    /// <summary>Proctor's <c>UserTurn.IsLoopNudge</c> (<c>Grade/Transcript.cs</c>) and the <c>loop_nudged</c> check.</summary>
    [Fact]
    public async Task LoopNudge_IsASystemReminderNamingARepetitiveLoop()
    {
        var result = await Nb.Program().Budget("tool_calls", 8).Run("MOCK:loop=bash echo hi").RunAsync(MockConfig(), Options());

        var nudges = result.Events.OfType<UserEvent>().Where(e => e.Text?.Contains("repetitive loop") == true).ToList();
        Assert.NotEmpty(nudges);
        Assert.All(nudges, n => Assert.StartsWith("<system_reminder>", n.Text));
    }

    /// <summary>
    /// Proctor's gate task <c>suites/gate/tasks/denied.json</c>: with no approval rules and
    /// <c>Trust: false</c>, a write is refused, and proctor's <c>ToolResult.IsError</c> sees the
    /// refusal by its <c>Error:</c> prefix.
    /// </summary>
    [Fact]
    public async Task UnapprovedWrite_IsDeniedWithAnErrorPrefix()
    {
        var result = await Nb.Program().Run("MOCK:tool=bash echo changed > note.txt").RunAsync(MockConfig(), Options());

        Assert.Equal("deny", result.Events.OfType<ToolCallEvent>().Single().Approved);
        Assert.StartsWith("Error:", result.Events.OfType<ToolResultEvent>().Single().Output);
    }

    /// <summary>Proctor's <c>ToolResult.IsError</c> reads a failed command by its trailing <c>[exit code: N]</c>.</summary>
    [Fact]
    public async Task FailedCommand_EndsWithAnExitCodeTrailer()
    {
        var result = await Nb.Program()
            .Add([new ApprovalEvent { Key = "bash", Value = "false" }])
            .Run("MOCK:tool=bash false")
            .RunAsync(MockConfig(), Options());

        Assert.Matches(@"\[exit code: 1\]\s*$", result.Events.OfType<ToolResultEvent>().Single().Output);
    }

    /// <summary>
    /// The trailer fields proctor reads on every run (<c>Grade/Transcript.cs</c> <c>Parse</c>,
    /// <c>Run/Ingest.cs</c>, and nb's own ATIF carries the trailer as <c>extra.result</c>).
    /// <c>denied</c>, <c>harness</c>, <c>oracle_turns</c> and <c>cost</c> are read too but written only when set;
    /// proctor defaults the counts to 0.
    /// </summary>
    [Fact]
    public async Task Trailer_CarriesTheFieldsProctorReads()
    {
        var r = await Nb.Program().Run("hello").RunAsync(MockConfig(), Options());
        var trailer = TranscriptMapper.ResultTrailer(r.Events, r.ExitReason, r.Usage, r.Harness, r.Denied, r.Provider, r.OracleTurns, r.OracleVerdict,
            (long)r.Duration.TotalMilliseconds, (long)r.ProviderTime.TotalMilliseconds, r.Model, r.ProgramSha256, r.NbVersion, r.Cost);

        var json = JsonNode.Parse(TranscriptSerializer.SerializeEvent(trailer))!.AsObject();
        Assert.Equal("result", json["type"]!.GetValue<string>());
        foreach (var field in new[] { "exit_reason", "turns", "tool_calls", "duration_ms", "provider", "model" })
            Assert.True(json.ContainsKey(field), $"trailer lacks '{field}', which proctor reads");
        foreach (var field in new[] { "input", "output", "total" })
            Assert.True(json["usage"]?.AsObject().ContainsKey(field) == true, $"trailer usage lacks '{field}', which proctor reads");
    }

    private static IConfiguration MockConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["Harness"] = "nb",
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
