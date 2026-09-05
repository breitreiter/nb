using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// bugs/Failed_Provider_Directive_Silently_Substitutes.md — a `provider` directive whose
/// client cannot be built used to leave the *previous* client live, so the run answered
/// from a provider the program never named and exited 0 with a clean transcript.
///
/// The repro is the report's own: two entries, one that builds (Mock) and one that does
/// not (an OpenAI entry with no ApiKey, which `CanCreate` rejects). Every test here was
/// confirmed failing against the unfixed evaluator.
/// </summary>
public class ProviderSubstitutionTests
{
    // Mock answers "from-mock"; "Broken" names a real implementation that cannot be
    // built because its required ApiKey is absent. Mock is active, so a run with no
    // provider directive succeeds and a `provider Broken` directive is the only variable.
    private static IConfiguration TwoEntryConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "from-mock",
            ["ChatProviders:1:Name"] = "Broken",
            ["ChatProviders:1:Provider"] = "OpenAI",
            ["ChatProviders:1:Model"] = "gpt-5",
        }).Build();

    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }

    /// <summary>
    /// The core defect: the run must not answer from Mock when the program asked for
    /// Broken. Before the fix this returned "from-mock" with exit_reason ok and exit 0.
    /// </summary>
    [Fact]
    public async Task UnbuildableProvider_AbortsInsteadOfAnsweringFromThePrevious()
    {
        var ex = await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => Nb.Program().Provider("Broken").Run("hi").RunAsync(TwoEntryConfig(), Options()));

        Assert.Contains("Broken", ex.Message);
    }

    /// <summary>
    /// The Notes section of the report: when the provider fails to build, the requested
    /// *model* is dropped with it, so `provider Broken` + `model gpt-5-mini` answered
    /// from Mock with neither of the things asked for. A benchmark sweeping
    /// provider/model pairs records the same baseline under several labels.
    /// </summary>
    [Fact]
    public async Task UnbuildableProvider_WithAModelDirective_AlsoAborts()
    {
        await Assert.ThrowsAsync<ProviderUnavailableException>(
            () => Nb.Program().Provider("Broken").Model("gpt-5-mini").Run("hi")
                    .RunAsync(TwoEntryConfig(), Options()));
    }

    /// <summary>A provider that builds is unaffected — the guard must not fire on the happy path.</summary>
    [Fact]
    public async Task BuildableProvider_StillRuns()
    {
        var result = await Nb.Program().Provider("Mock").Run("hi").RunAsync(TwoEntryConfig(), Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.Equal("from-mock", result.Answer);
    }

    /// <summary>
    /// Attribution: the transcript must say which provider actually answered, so a
    /// corpus of runs cannot be mis-attributed. Mirrors how the trailer already records
    /// `harness` (docs/conversation-program-cli.md:180).
    /// </summary>
    [Fact]
    public async Task Trailer_RecordsTheProviderThatAnswered()
    {
        var result = await Nb.Program().Provider("Mock").Run("hi").RunAsync(TwoEntryConfig(), Options());

        Assert.Equal("Mock", result.Provider);
    }

    /// <summary>A run with no `provider` directive still records the provider it resolved to.</summary>
    [Fact]
    public async Task Trailer_RecordsTheProvider_WhenTheProgramNamesNone()
    {
        var result = await Nb.Program().Run("hi").RunAsync(TwoEntryConfig(), Options());

        Assert.Equal("Mock", result.Provider);
    }
}
