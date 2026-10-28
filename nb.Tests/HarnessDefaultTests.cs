using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// nb's own surface is the only harness, and the default: a run that names none anywhere
/// runs on it. A retired costume named by the program, the provider entry, top-level
/// config or the host's options is refused before anything reaches a model, with the
/// redirect to the real harness (plans/atif-and-acp.md stage 1).
/// </summary>
public class HarnessDefaultTests
{
    private static IConfiguration Config(string? entryHarness = null, string? topHarness = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
        };
        if (entryHarness != null) settings["ChatProviders:0:Harness"] = entryHarness;
        if (topHarness != null) settings["Harness"] = topHarness;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static NbOptions Options(string? harness = null)
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers"), Harness = harness };
    }

    [Fact]
    public async Task NoHarnessAnywhere_RunsOnNb()
    {
        var result = await Nb.Program().Run("hi").RunAsync(Config(), Options());

        Assert.Equal(ExitReasons.Ok, result.ExitReason);
        Assert.Null(result.Harness);
        Assert.DoesNotContain(result.Events, e => e is SystemEvent);
    }

    [Fact]
    public async Task ExplicitNb_Runs()
    {
        var result = await Nb.Program().Harness("nb").Run("hi").RunAsync(Config(), Options());

        Assert.Equal(ExitReasons.Ok, result.ExitReason);
        Assert.Null(result.Harness);
    }

    [Theory]
    [InlineData("claude-code", "Claude Code")]
    [InlineData("codex", "Codex")]
    [InlineData("qwen-code", "Qwen Code")]
    public async Task RetiredEntryHarness_IsRefusedWithTheRedirect(string costume, string product)
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(entryHarness: costume), Options()));

        Assert.Equal(
            $"harness {costume} was retired on 2026-10-28. To test in {product}, run {product}: "
            + $"a proctor arm with \"agent\": \"{costume}\". To run this model on nb's own surface, "
            + "write harness nb, or name no harness.",
            ex.Message);
    }

    [Fact]
    public async Task RetiredTopLevelHarness_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(topHarness: "codex"), Options()));

        Assert.StartsWith("harness codex was retired", ex.Message);
    }

    [Fact]
    public async Task RetiredHostOption_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(), Options(harness: "claude-code")));

        Assert.StartsWith("harness claude-code was retired", ex.Message);
    }

    [Fact]
    public async Task ProgramDirective_BeatsARetiredConfig()
    {
        var result = await Nb.Program().Harness("nb").Run("hi").RunAsync(Config(entryHarness: "qwen-code"), Options());

        Assert.Equal(ExitReasons.Ok, result.ExitReason);
    }

    /// <summary>JSONL bytecode skips the parser, so the evaluator refuses a retired name itself.</summary>
    [Fact]
    public async Task RetiredHarnessInBytecode_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Add([new HarnessEvent { Name = "codex" }]).Run("hi").RunAsync(Config(), Options()));

        Assert.StartsWith("harness codex was retired", ex.Message);
    }

    [Fact]
    public async Task UnknownConfigHarness_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(entryHarness: "cursor"), Options()));

        Assert.Equal("unknown harness 'cursor'. The only harness is nb, which is the default.", ex.Message);
    }
}
