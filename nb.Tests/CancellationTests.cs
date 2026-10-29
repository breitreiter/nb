using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// A caller's cancel ends the run cleanly (<c>plans/atif-and-acp.md</c> stage 4): an outcome
/// on the result, <c>cancelled</c>, and a transcript that still reads, rather than an
/// exception that loses both. ACP's <c>session/cancel</c> is the first caller that needs it.
/// </summary>
public class CancellationTests
{
    [Fact]
    public async Task AnAlreadyCancelledToken_EndsTheRunCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await Nb.Program().Run("hello").RunAsync(MockConfig(), Options(), cts.Token);

        Assert.Equal(ExitReasons.Cancelled, result.ExitReason);
        Assert.Equal(130, result.ExitCode);
        Assert.Equal("cancelled", result.Trailer().ExitReason);
    }

    // The bug half: a command running when the cancel arrived used to run to its own timeout.
    [Fact]
    public async Task CancelMidCommand_KillsIt_AndPairsTheCallWithAResult()
    {
        // Late enough that engine startup is over and the command is running.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var clock = Stopwatch.StartNew();

        var result = await Nb.Program()
            .Add([new ApprovalEvent { Key = "bash", Value = "sleep *" }])
            .Run("MOCK:tool=bash sleep 30")
            .Run("a second run that never starts")
            .RunAsync(MockConfig(), Options(), cts.Token);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Equal(ExitReasons.Cancelled, result.ExitReason);
        var call = Assert.Single(result.Events.OfType<ToolCallEvent>());
        var output = Assert.Single(result.Events.OfType<ToolResultEvent>(), r => r.Id == call.Id).Output;
        Assert.StartsWith("Error:", output);
        Assert.DoesNotContain(result.Events.OfType<UserEvent>(), u => u.Text == "a second run that never starts");
    }

    [Fact]
    public async Task BashTool_KillsTheProcessTree_OnCancel()
    {
        var bash = new BashTool(ShellEnvironment.Detect(), defaultTimeoutSeconds: 60);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bash.ExecuteAsync("sleep 30", cancellationToken: cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    // A wall-clock budget runs on the same token, so it now stops a command too.
    [Fact]
    public async Task WallBudget_StopsARunningCommand_AsTimeBudget()
    {
        var clock = Stopwatch.StartNew();
        var result = await Nb.Program()
            .Add([new ApprovalEvent { Key = "bash", Value = "sleep *" }])
            .Budget("wall_ms", 500)
            .Run("MOCK:tool=bash sleep 30")
            .RunAsync(MockConfig(), Options());

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Equal(ExitReasons.TimeBudget, result.ExitReason);
    }

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
