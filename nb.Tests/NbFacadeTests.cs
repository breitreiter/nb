using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

// Exercises the in-process library facade end-to-end against the Mock provider.
// The test host is itself a "library host" — its AppContext.BaseDirectory has no
// providers/ — so it points NbOptions.ProvidersDirectory at nb's built providers
// dir, which is exactly the Phase 6b library-host loading contract. One class so
// the global AnsiConsole/Console swaps inside Nb.RunAsync never race across tests.
[Collection(ConsoleBoundCollection.Name)]
public class NbFacadeTests
{
    private static IConfiguration MockConfig(string activeProvider = "Mock", string response = "OK")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = activeProvider,
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = response,
        }).Build();

    // nb's providers live next to nb's Exe (bin/<Config>/net10.0/providers), not next
    // to the test assembly — derive that path from the test's own output location.
    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);                            // net10.0
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!); // Debug | Release
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }

    [Fact]
    public async Task BasicRun_ReturnsAnswerUsageAndOkOutcome()
    {
        var result = await Nb.Program().Run("hello").RunAsync(MockConfig(), Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("OK", result.Answer);
        Assert.NotNull(result.Usage);
        Assert.Equal(15, result.Usage!.Total);
        // The completed conversation round-trips as typed events.
        Assert.Contains(result.Events, e => e is UserEvent);
        Assert.Contains(result.Events, e => e is AssistantTextEvent);
    }

    [Fact]
    public async Task ProviderError_IsAnOutcome_NotAnException()
    {
        // MOCK:throw makes the provider fail mid-turn. That's a run outcome carried in
        // the result — never a thrown exception from the facade.
        var result = await Nb.Program().Run("MOCK:throw").RunAsync(MockConfig(), Options());

        Assert.Equal("provider_error", result.ExitReason);
        Assert.Equal(2, result.ExitCode);
    }

    // A transient throttle is absorbed: the run answers normally instead of dying
    // and throwing away every tool call it had already made.
    [Fact]
    public async Task RateLimit_IsRetriedAndTheRunSucceeds()
    {
        var config = MockConfig();
        var result = await Nb.Program().Run("MOCK:ratelimit=2").RunAsync(config, Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("recovered", result.Answer);
    }

    // A sustained throttle still ends the run, but as its own retryable outcome —
    // exit 3, distinct from provider_error, so a harness knows to back off and re-run.
    [Fact]
    public async Task RateLimit_WhenRetriesAreExhausted_IsItsOwnExitReason()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:MaxRetries"] = "2",
            ["ChatProviders:0:RetryMaxDelaySeconds"] = "1",
        }).Build();

        var result = await Nb.Program().Run("MOCK:ratelimit").RunAsync(config, Options());

        Assert.Equal("rate_limited", result.ExitReason);
        Assert.Equal(3, result.ExitCode);
    }

    // MaxRetries: 0 opts out — the first throttle ends the run immediately.
    [Fact]
    public async Task RateLimit_WithRetriesDisabled_FailsImmediately()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:MaxRetries"] = "0",
        }).Build();

        var result = await Nb.Program().Run("MOCK:ratelimit=1").RunAsync(config, Options());

        Assert.Equal("rate_limited", result.ExitReason);
    }

    [Fact]
    public async Task UnbuildableClient_ThrowsNbStartupException()
    {
        // An ActiveProvider with no matching, loadable provider can't produce a client.
        await Assert.ThrowsAsync<NbStartupException>(() =>
            Nb.Program().Run("hi").RunAsync(MockConfig(activeProvider: "NoSuchProvider"), Options()));
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsException()
    {
        // The CancellationToken passed to RunAsync is honored (it used to be dropped):
        // an already-cancelled token surfaces as an OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Nb.Program().Run("hello").RunAsync(MockConfig(), Options(), cts.Token));
    }

    [Fact]
    public async Task WallClockBudget_AbortsWithTimeBudget()
    {
        // A 1ms wall-clock ceiling can't outlast the mock's per-call delay, so the run
        // aborts as a clean outcome (not an exception) with time_budget / exit 3.
        var result = await Nb.Program().Run("hello")
            .RunAsync(MockConfig(), Options() with { WallClockBudgetMs = 1 });

        Assert.Equal("time_budget", result.ExitReason);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task PartialUsage_DerivesTotalFromTheParts()
    {
        // MOCK:partialusage reports input and output but no total — the shape a
        // normalizing gateway (or the Anthropic API, which has no total_tokens field at
        // all) produces. The total is derived, and it's still a measurement, not a guess.
        var result = await Nb.Program().Run("MOCK:partialusage").RunAsync(MockConfig(), Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.Equal(15, result.Usage!.Total);
        Assert.False(result.Usage.Estimated);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("estimated"));
    }

    [Fact]
    public async Task NoUsageReported_EstimatesAndFlagsIt()
    {
        // MOCK:nousage drops the usage chunk the way a proxy that ignores
        // stream_options.include_usage does. nb estimates rather than counting zero, and
        // says so — on the trailer and in the warnings.
        var result = await Nb.Program().Run("MOCK:nousage").RunAsync(MockConfig(), Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.NotNull(result.Usage);
        Assert.True(result.Usage!.Estimated);
        Assert.True(result.Usage.Total > 0, "an estimated round must not count as zero");
        Assert.Contains(result.Warnings, w => w.Contains("estimated"));
    }

    [Fact]
    public async Task TokenBudget_IsEnforcedAgainstEstimates()
    {
        // The point of estimating: a budget stays enforceable behind a usage-blind
        // endpoint. The first run spends past the 1-token ceiling on estimate alone, so
        // the second never reaches the provider.
        var result = await Nb.Program()
            .Budget("tokens", 1)
            .Run("MOCK:nousage")
            .Run("MOCK:nousage")
            .RunAsync(MockConfig(), Options());

        Assert.Equal("token_budget", result.ExitReason);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task Run_DoesNotLeakToStdout()
    {
        // The chrome-suppression contract: with the default (null) diagnostics sink,
        // nothing reaches stdout — the caller owns their stdout.
        var original = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await Nb.Program().Run("hello").RunAsync(MockConfig(), Options());
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal("", captured.ToString());
    }
}
