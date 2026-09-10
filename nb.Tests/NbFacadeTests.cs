using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

// Exercises the in-process library facade end-to-end against the Mock provider.
// The test host is itself a "library host" — its AppContext.BaseDirectory has no
// providers/ — so it points NbOptions.ProvidersDirectory at nb's built providers
// dir, which is exactly the Phase 6b library-host loading contract.
public class NbFacadeTests
{
    private static IConfiguration MockConfig(string activeProvider = "Mock", string response = "OK")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = activeProvider,
            ["Harness"] = "nb",
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
            ["Harness"] = "nb",
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
            ["Harness"] = "nb",
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

    // ---- The Mock oracle convention (plans/oracle-resolver.md, build order step 2) ----
    //
    // Settled BEFORE the resolver exists, because it is what makes steps 3-4 testable at
    // all: discovering the convention doesn't work after the loop is built means
    // rewriting both. The resolver does not issue oracle calls yet, so these drive an
    // oracle-SHAPED call directly — a run whose prompt opens with the sentinel, which is
    // exactly the contract step 3 must honour when it builds the real call.

    private static async Task<string?> OracleVerdict(string shownToTheOracle)
        => (await Nb.Program()
                .Run(OracleProtocol.Sentinel + "\nJudge this:\n" + shownToTheOracle)
                .RunAsync(MockConfig(), Options())).Answer;

    [Fact]
    public async Task Oracle_RiderSelectsSheetEntries()
        => Assert.Equal("deploy-target", await OracleVerdict("Which environment? MOCK:oracle=deploy-target"));

    [Fact]
    public async Task Oracle_RiderCarriesMultipleIds()
        => Assert.Equal("deploy-target,customer-name",
            await OracleVerdict("Two questions. MOCK:oracle=deploy-target,customer-name and more prose"));

    [Theory]
    [InlineData(OracleProtocol.Done)]
    [InlineData(OracleProtocol.Miss)]
    public async Task Oracle_RiderCarriesTheTerminalVerdicts(string verdict)
        => Assert.Equal(verdict, await OracleVerdict($"Some reply. MOCK:oracle={verdict}"));

    [Fact]
    public async Task Oracle_WithNoRider_DefaultsToDone()
    {
        // The design's "when in doubt, DONE": an unscripted program must not continue by
        // accident. Without this the call falls through to the Mock's default response,
        // which is not a parseable verdict.
        Assert.Equal(OracleProtocol.Done, await OracleVerdict("An ordinary reply with nothing scripted."));
    }

    [Fact]
    public async Task Oracle_RiderIsInert_OnAnOrdinaryTurn()
    {
        // The rider only means anything on a call the sentinel marks as an oracle call.
        // On a normal turn it is just text, so the subject's scripted reply is unchanged
        // — which is what lets one program line script both halves.
        var result = await Nb.Program()
            .Run("MOCK:response=Which environment should I deploy to? MOCK:oracle=deploy-target")
            .RunAsync(MockConfig(), Options());

        Assert.Equal("ok", result.ExitReason);
        Assert.Equal("Which environment should I deploy to? MOCK:oracle=deploy-target", result.Answer);
    }

    [Fact]
    public async Task Oracle_OneProgramLineScriptsBothHalves()
    {
        // The end-to-end property step 3 will lean on: the subject's scripted reply is
        // what the oracle gets shown, and the rider rides along inside it. Asserted as
        // two halves joined by hand, because nothing issues the real call yet.
        var subject = await Nb.Program()
            .Run("MOCK:response=Which environment should I deploy to? MOCK:oracle=deploy-target")
            .RunAsync(MockConfig(), Options());

        Assert.Equal("deploy-target", await OracleVerdict(subject.Answer!));
    }

    // ---- The oracle-aware repetition-breaker (plans/oracle-resolver.md, step 4) ----
    //
    // The doom-loop reminder tells a stuck model "No one is available to answer a
    // question mid-run." That is true on a bare program and false the moment an oracle is
    // declared — so the reminder has to know, and the string a model reads is the thing
    // to pin.

    private static async Task<string> LoopReminder(NbProgramBuilder program)
    {
        var result = await program.Budget("tool_calls", 8).Run("MOCK:loop=bash echo hi").RunAsync(MockConfig(), Options());
        return result.Events.OfType<UserEvent>().First(e => e.Text?.Contains("repetitive loop") == true).Text!;
    }

    [Fact]
    public async Task LoopReminder_WithoutAnOracle_SaysNobodyIsHome()
        => Assert.Contains("No one is available to answer a question mid-run.", await LoopReminder(Nb.Program()));

    [Fact]
    public async Task LoopReminder_WithAnOracle_SaysToAskPlainly()
    {
        var text = await LoopReminder(Nb.Program().Oracle("## t\nbody\n"));
        Assert.DoesNotContain("No one is available", text);
        Assert.Contains("end the turn and ask", text);
    }

    // ---- The resolver end to end (plans/oracle-resolver.md, steps 3-4) ----
    //
    // Each sheet body is also the subject's next scripted turn, so a body that opens with
    // MOCK:response= chains one scripted question into the next; a body without one gets
    // the Mock's default reply, which carries no rider and so judges DONE.

    private const string Sheet = """
        ## deploy-target
        Staging only. Never touch prod during this exercise.

        ## customer-name
        MOCK:response=Thanks. Which environment? MOCK:oracle=deploy-target

        ## again
        MOCK:response=And again? MOCK:oracle=again
        """;

    private static Task<RunResult> Oracle(string prompt, long? turns = null)
    {
        var program = Nb.Program().Oracle(Sheet);
        if (turns is { } t) program.Budget("oracle_turns", t);
        return program.Run(prompt).RunAsync(MockConfig(), Options());
    }

    [Fact]
    public async Task Oracle_Hit_AppendsTheSheetBodyAsAUserTurn_AndRunsAgain()
    {
        var r = await Oracle("MOCK:response=Which environment should I deploy to? MOCK:oracle=deploy-target");

        Assert.Equal("ok", r.ExitReason);
        Assert.Equal(1, r.OracleTurns);
        var answer = Assert.Single(r.Events.OfType<UserEvent>(), u => u.Source == "oracle");
        Assert.Equal("Staging only. Never touch prod during this exercise.", answer.Text);
        Assert.Equal(new[] { "deploy-target" }, answer.Keys);
        // The subject ran again on the answer: a second assistant turn follows it.
        Assert.Equal("OK", r.Answer);
        // Ordinary user turns carry no enrichment.
        Assert.Null(r.Events.OfType<UserEvent>().First().Source);
    }

    [Fact]
    public async Task Oracle_ChainsThroughSeveralAsks()
    {
        var r = await Oracle("MOCK:response=Customer? MOCK:oracle=customer-name");
        Assert.Equal("ok", r.ExitReason);
        Assert.Equal(2, r.OracleTurns);
        Assert.Equal(new[] { "customer-name", "deploy-target" },
            r.Events.OfType<UserEvent>().Where(u => u.Source == "oracle").Select(u => u.Keys!.Single()));
    }

    [Fact]
    public async Task Oracle_Miss_EndsTheRun_ExitZero_LabelDiffers()
    {
        var r = await Oracle("MOCK:response=What is the meaning of life? MOCK:oracle=MISS");
        Assert.Equal("oracle_miss", r.ExitReason);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal(0, r.OracleTurns);
        // Never paper over the ask: the question is the last thing in the transcript.
        Assert.Equal("What is the meaning of life? MOCK:oracle=MISS", r.Answer);
        Assert.Contains(r.Warnings, w => w.Contains("oracle_miss"));
    }

    [Fact]
    public async Task Oracle_Done_EndsTheRunAsOk_WithNoOracleTurns()
    {
        var r = await Oracle("MOCK:response=Done. Want me to also do X? MOCK:oracle=DONE");
        Assert.Equal("ok", r.ExitReason);
        Assert.Equal(0, r.OracleTurns);
        Assert.DoesNotContain(r.Events.OfType<UserEvent>(), u => u.Source == "oracle");
    }

    [Fact]
    public async Task Oracle_Budget_EndsTheRun_Exit3()
    {
        var r = await Oracle("MOCK:response=And again? MOCK:oracle=again", turns: 3);
        Assert.Equal("oracle_budget", r.ExitReason);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal(3, r.OracleTurns);
        Assert.Equal(3, r.Events.OfType<UserEvent>().Count(u => u.Source == "oracle"));
    }

    [Fact]
    public async Task Oracle_WithoutADirective_TheRiderIsInertAndNothingContinues()
    {
        var r = await Nb.Program().Run("MOCK:response=Which environment? MOCK:oracle=deploy-target").RunAsync(MockConfig(), Options());
        Assert.Equal("ok", r.ExitReason);
        Assert.Equal(0, r.OracleTurns);
        Assert.Equal(2, r.Events.Count);
    }

    [Fact]
    public async Task Oracle_IsNotConsulted_WhenTheRunDidNotEndOk()
    {
        // A provider failure is the reason to keep; the oracle would only be noise on it.
        var r = await Nb.Program().Oracle(Sheet).Run("MOCK:throw").RunAsync(MockConfig(), Options());
        Assert.Equal("provider_error", r.ExitReason);
        Assert.Equal(0, r.OracleTurns);
    }
}
