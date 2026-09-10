using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// Every run wears a harness on purpose. The bare surface used to be what a program got
/// by forgetting the directive, which produced runs labelled "qwen-code" that were
/// harness=nb model=qwen — hours lost chasing behaviour that was the missing costume,
/// not the model. Now a run with no harness in the program, the provider entry, or the
/// host's options is refused before anything reaches a model; `harness nb` is how you
/// ask for the bare surface by name.
/// </summary>
public class HarnessRequiredTests
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
    public async Task NoHarnessAnywhere_IsRefusedBeforeTheModelIsCalled()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(), Options()));

        Assert.Contains("no harness named", ex.Message);
        Assert.Contains("harness nb", ex.Message);
        Assert.Contains("claude-code", ex.Message);
    }

    [Fact]
    public async Task ExplicitBareSurface_Runs()
    {
        var result = await Nb.Program().Harness("nb").Run("hi").RunAsync(Config(), Options());

        Assert.Equal(ExitReasons.Ok, result.ExitReason);
        Assert.Null(result.Harness);
        Assert.DoesNotContain(result.Events, e => e is SystemEvent);
    }

    [Fact]
    public async Task ProviderEntryHarness_IsWornAndRecorded()
    {
        var result = await Nb.Program().Run("hi").RunAsync(Config(entryHarness: "qwen-code"), Options());

        Assert.Equal("qwen-code", result.Harness);
        Assert.Contains(result.Events, e => e is SystemEvent);
    }

    [Fact]
    public async Task TopLevelHarness_IsTheFallbackForAnEntryThatNamesNone()
    {
        var result = await Nb.Program().Run("hi").RunAsync(Config(topHarness: "codex"), Options());

        Assert.Equal("codex", result.Harness);
    }

    [Fact]
    public async Task ProgramDirective_BeatsConfig()
    {
        var result = await Nb.Program().Harness("nb").Run("hi").RunAsync(Config(entryHarness: "qwen-code"), Options());

        Assert.Null(result.Harness);
    }

    [Fact]
    public async Task HostOption_BeatsConfig()
    {
        var result = await Nb.Program().Run("hi").RunAsync(Config(entryHarness: "qwen-code"), Options(harness: "codex"));

        Assert.Equal("codex", result.Harness);
    }

    [Fact]
    public async Task UnknownConfigHarness_IsRefusedWithTheKnownNames()
    {
        var ex = await Assert.ThrowsAsync<NbStartupException>(
            () => Nb.Program().Run("hi").RunAsync(Config(entryHarness: "cursor"), Options()));

        Assert.Contains("unknown harness 'cursor'", ex.Message);
        Assert.Contains("qwen-code", ex.Message);
    }
}
