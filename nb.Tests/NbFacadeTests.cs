using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

// Exercises the in-process library facade end-to-end against the Mock provider
// (copied into the test output by the CopyMockProvider target). One class so the
// global AnsiConsole/Console swaps inside Nb.RunAsync never race across tests.
public class NbFacadeTests
{
    private static IConfiguration MockConfig(string activeProvider = "Mock", string response = "OK")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = activeProvider,
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = response,
        }).Build();

    [Fact]
    public async Task BasicRun_ReturnsAnswerUsageAndOkOutcome()
    {
        var result = await Nb.Program().Run("hello").RunAsync(MockConfig());

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
        var result = await Nb.Program().Run("MOCK:throw").RunAsync(MockConfig());

        Assert.Equal("provider_error", result.ExitReason);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public async Task UnbuildableClient_ThrowsNbStartupException()
    {
        // An ActiveProvider with no matching, loadable provider can't produce a client.
        await Assert.ThrowsAsync<NbStartupException>(() =>
            Nb.Program().Run("hi").RunAsync(MockConfig(activeProvider: "NoSuchProvider")));
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
            await Nb.Program().Run("hello").RunAsync(MockConfig());
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal("", captured.ToString());
    }
}
