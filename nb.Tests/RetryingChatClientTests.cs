using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb;

namespace nb.Tests;

// The retry wrapper is what stands between a transient gateway throttle and a dead
// run that has already paid for 40 turns of tool calls. These tests keep the timing
// knobs tiny (1s cap, ~2s budget) so the real waits stay in the test's patience.
[Collection(ConsoleBoundCollection.Name)]
public class RetryingChatClientTests
{
    private static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static IChatClient Wrap(IChatClient inner, params (string Key, string Value)[] settings)
    {
        var config = Config(settings);
        return RetryingChatClient.Wrap(inner, config, config);
    }

    // Throws a throttle rejection for the first N calls, then answers normally.
    private sealed class ThrottlingChatClient(int failures) : IChatClient
    {
        public int Calls { get; private set; }
        public List<DateTimeOffset> CallTimes { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            CallTimes.Add(DateTimeOffset.UtcNow);
            if (Calls <= failures)
                throw new InvalidOperationException("Wholesale rate limit exceeded for this gateway.");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public void MaxRetriesZero_ReturnsTheClientUntouched()
    {
        var inner = new ThrottlingChatClient(0);
        Assert.Same(inner, Wrap(inner, ("MaxRetries", "0")));
    }

    [Fact]
    public async Task Throttle_IsRetriedUntilItSucceeds()
    {
        var inner = new ThrottlingChatClient(failures: 2);
        var client = Wrap(inner, ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "30"));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("ok", response.Text);
        Assert.Equal(3, inner.Calls);
    }

    // The regression that killed real runs was an attempt ladder that gave up in ~30s
    // against a limit that lasts minutes. The budget is the stop condition now, so a
    // generous attempt cap must not turn into an unbounded wait.
    [Fact]
    public async Task RetriesStopAtTheWallClockBudget_NotTheAttemptCap()
    {
        var inner = new ThrottlingChatClient(failures: int.MaxValue);
        var client = Wrap(inner,
            ("MaxRetries", "1000"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "2"));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.InRange(inner.Calls, 2, 10);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"gave up after {elapsed.TotalSeconds:0.#}s");
    }

    [Fact]
    public async Task AttemptCapStillBounds_WhenTheBudgetIsLarge()
    {
        var inner = new ThrottlingChatClient(failures: int.MaxValue);
        var client = Wrap(inner,
            ("MaxRetries", "2"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "600"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(3, inner.Calls); // the initial call plus two retries
    }

    // Retrying only the throttled call leaves the next turn free to charge straight
    // back into the same limit. After a throttle, subsequent requests are paced.
    [Fact]
    public async Task AfterAThrottle_TheNextRequestIsPaced()
    {
        var inner = new ThrottlingChatClient(failures: 1);
        var client = Wrap(inner, ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "30"));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        var afterFirstTurn = DateTimeOffset.UtcNow;
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "again")]);

        var gap = inner.CallTimes[^1] - afterFirstTurn;
        Assert.True(gap > TimeSpan.FromMilliseconds(200), $"second turn was not paced (gap {gap.TotalMilliseconds:0}ms)");
    }

    [Fact]
    public async Task WithoutAThrottle_NothingIsPaced()
    {
        var inner = new ThrottlingChatClient(failures: 0);
        var client = Wrap(inner);

        var started = DateTimeOffset.UtcNow;
        for (var i = 0; i < 5; i++)
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(5, inner.Calls);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(1));
    }
}
