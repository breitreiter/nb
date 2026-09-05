using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Harness;
using nb.MCP;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// Two runs in one process must both reach the model
/// (bugs/Concurrent_Runs_Collide_On_The_Global_Console.md). Spectre's <c>AnsiConsole</c>
/// is process-global and permits one live display at a time, so the "Thinking…" spinner
/// on the second run threw, the throw landed in the turn's own catch, and the run
/// returned having never called the client — chrome cancelling a model call.
///
/// <para>The workaround this replaces was <c>ConsoleBoundCollection</c>, an xunit
/// collection that put every run-driving test class into one serialised group. It was a
/// real fix for the tests and no fix at all for a library host. It is deleted, and the
/// suite staying green without it is the load-bearing verification here — a stronger
/// signal than these two tests, because it is ten classes racing for real.</para>
///
/// <para>The race is made deterministic rather than probable. The first run's client
/// blocks inside the streaming call, so it provably owns the live display before the
/// second run starts; the second therefore always meets a display already open, which is
/// the losing condition. No sleeps, no timing margin.</para>
/// </summary>
public class ConcurrentRunTests
{
    [Fact]
    public async Task SecondConcurrentRun_StillReachesTheModel()
    {
        var client = new GatedChatClient();

        var first = Task.Run(() => Manager(client).RunAsync("first"));

        // The first run is now inside GetStreamingResponseAsync, holding the spinner.
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Start the second while the display is definitely open.
        var secondManager = Manager(client);
        var second = Task.Run(() => secondManager.RunAsync("second"));
        await second.WaitAsync(TimeSpan.FromSeconds(30));

        client.Release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, client.Calls);
    }

    [Fact]
    public async Task ManyConcurrentRuns_AllReachTheModel()
    {
        // The library surface is documented as "one contract, three surfaces" and says
        // nothing about being single-threaded; fanning a matrix of programs out in one
        // process is the obvious way to use it.
        var client = new GatedChatClient { GateFirstCall = false };

        await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(i => Task.Run(() => Manager(client).RunAsync($"run {i}"))));

        Assert.Equal(8, client.Calls);
    }

    [Fact]
    public async Task ConcurrentFacadeRuns_LeaveTheGlobalConsoleAsTheyFoundIt()
    {
        // Nb.RunAsync redirects the process-global AnsiConsole.Console and restored it with
        // a plain saved local, so two overlapping runs each saved what the other had set and
        // the last one out restored a writer belonging to a finished run — the console stayed
        // pointed at a dead sink after both calls returned, which outlives the runs entirely.
        var original = AnsiConsole.Console;

        await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => Nb.Program().Run("hi").RunAsync(FacadeConfig(), FacadeOptions())));

        Assert.Same(original, AnsiConsole.Console);
    }

    private static IConfiguration FacadeConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
        }).Build();

    // nb's providers live next to nb's Exe, not next to the test assembly.
    private static NbOptions FacadeOptions()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }

    private static ConversationManager Manager(IChatClient client) =>
        new(client, new McpManager(), new FakeToolManager(),
            new NbHarness(), new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false));

    /// <summary>
    /// Blocks inside the first streaming call until released, so a second run is
    /// guaranteed to overlap it. Later calls pass straight through.
    /// </summary>
    private sealed class GatedChatClient : IChatClient
    {
        private int _calls;

        public bool GateFirstCall { get; init; } = true;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1 && GateFirstCall)
            {
                Entered.SetResult();
                await Release.Task;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
