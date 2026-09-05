using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// `bash` advertises a timeout, so a model that sets one must get it
/// (bugs/Bash_Advertises_A_Timeout_It_Ignores.md). Three things had to be true and none
/// of them were: the dispatch path has to read the argument, the schema has to let it be
/// omitted, and the configured default has to be a default rather than a ceiling. The
/// first is pinned here, the second by ToolSurfaceGoldenTests, and the third by
/// RequestedTimeoutAboveDefault_RaisesTheEffectiveTimeout — the one assertion that
/// catches a half-fix, since wiring the argument through alone leaves Math.Min in place.
/// </summary>
public class BashTimeoutTests
{
    private static readonly ShellEnvironment Env = ShellEnvironment.Detect(Array.Empty<string>());

    private static BashTool Tool(int def, int max = 600) =>
        new(Env, defaultTimeoutSeconds: def, maxTimeoutSeconds: max);

    // --- The clamp: the configured value is a default, not a ceiling ---

    [Fact]
    public async Task RequestedTimeoutAboveDefault_RaisesTheEffectiveTimeout()
    {
        // Math.Min(requested, default) made the configured default a ceiling, so a model
        // asking for longer on a slow build silently got the default and a truncated run
        // it could not diagnose.
        var result = await Tool(def: 2).ExecuteAsync("sleep 4; echo done", cwd: Path.GetTempPath(), timeoutSeconds: 30);

        Assert.False(result.TimedOut);
        Assert.Contains("done", result.Stdout);
    }

    [Fact]
    public async Task RequestedTimeoutBelowDefault_StillLowersIt()
    {
        var result = await Tool(def: 60).ExecuteAsync("sleep 10", cwd: Path.GetTempPath(), timeoutSeconds: 1);

        Assert.True(result.TimedOut);
        Assert.Contains("exceeded 1s timeout", result.Stdout);
    }

    [Fact]
    public async Task RequestedTimeoutAboveTheMaximum_IsCappedAtTheMaximum()
    {
        // The raise is bounded: a model cannot park a headless run for an hour.
        var result = await Tool(def: 1, max: 2).ExecuteAsync("sleep 10", cwd: Path.GetTempPath(), timeoutSeconds: 9999);

        Assert.True(result.TimedOut);
        Assert.Contains("exceeded 2s timeout", result.Stdout);
    }

    [Fact]
    public async Task ConfiguredDefaultAboveTheMaximum_IsHonouredNotClampedDown()
    {
        // An operator who configures a long default means it; the cap exists to bound what
        // the *model* can ask for, not to overrule config.
        var result = await Tool(def: 4, max: 1).ExecuteAsync("sleep 2; echo done", cwd: Path.GetTempPath());

        Assert.False(result.TimedOut);
        Assert.Contains("done", result.Stdout);
    }

    // --- The dispatch path reads the argument at all ---

    private static async Task<string> Dispatch(string? costume, string wireName, IDictionary<string, object?> args)
    {
        NbHarness instance = new NbHarness(Tool(def: 2));
        if (costume is not null) instance = HarnessRegistry.Create(costume, instance);
        instance.Configure(
            new ApprovalPolicy(trust: false, new ApprovalPatterns(new[] { "*" }), _ => false, null, ApprovalDefault.Prompt),
            verbose: false, new ApprovalLedger());

        var outcome = await instance.InvokeAsync(wireName, "call-1", args, CancellationToken.None);
        return outcome!.Value.Content.Result?.ToString() ?? "";
    }

    [Fact]
    public async Task NativeBash_ReadsTimeoutSecondsFromTheCall()
    {
        // Default is 2s; the call asks for 30. Before the dispatch wiring the argument was
        // accepted and discarded, so this timed out.
        var output = await Dispatch(null, "bash", new Dictionary<string, object?>
        {
            ["command"] = "sleep 4; echo done",
            ["description"] = "sleep past the default",
            ["timeout_seconds"] = 30,
        });

        Assert.Contains("done", output);
        Assert.DoesNotContain("exceeded", output);
    }

    [Theory]
    [InlineData("codex", "shell_command", "timeout_ms")]
    [InlineData("claude-code", "Bash", "timeout")]
    [InlineData("qwen-code", "run_shell_command", "timeout")]
    public async Task Costumes_ConvertTheirMillisecondTimeout(string costume, string wireName, string key)
    {
        // All three targets declare their timeout in milliseconds. nb's is in seconds, and
        // nothing converted between them because the shared capability took no timeout at
        // all — so each costume advertised a parameter it could not honour.
        var output = await Dispatch(costume, wireName, new Dictionary<string, object?>
        {
            ["command"] = "sleep 4; echo done",
            ["description"] = "sleep past the default",
            [key] = 30000,
        });

        Assert.Contains("done", output);
        Assert.DoesNotContain("exceeded", output);
    }
}
