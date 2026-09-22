using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb;

namespace nb.Tests;

// The retry wrapper is what stands between a transient gateway throttle and a dead
// run that has already paid for 40 turns of tool calls. These tests keep the timing
// knobs tiny (1s cap, ~2s budget) so the real waits stay in the test's patience.
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

    // Was MaxRetriesZero_ReturnsTheClientUntouched, asserting Wrap handed the inner
    // client straight back. The wrapper is now installed unconditionally, because it is
    // also what measures provider_ms and an unwrapped client would report 0 rather than
    // "unmeasured". The contract that actually matters is unchanged and is what this now
    // asserts: MaxRetries 0 opts out of retrying, whatever the object graph looks like.
    [Fact]
    public async Task MaxRetriesZero_DoesNotRetry()
    {
        var inner = new ThrottlingChatClient(failures: int.MaxValue);
        var client = Wrap(inner, ("MaxRetries", "0"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(1, inner.Calls);
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
        // RetryMaxDelaySeconds also caps the adaptive pace, and the pace is no longer
        // charged to the budget — so without pinning it this test's wall clock is set by
        // RaisePace's ladder rather than by the thing it is asserting. 0 keeps it honest.
        var inner = new ThrottlingChatClient(failures: int.MaxValue);
        var client = Wrap(inner,
            ("MaxRetries", "1000"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "2"),
            ("MinRequestIntervalMs", "0"));

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

    // 104 of 150 calls in one run were throttled with the adaptive pace alone: it halves
    // on every clean response, so two successes put a run back at full speed against a
    // gateway limit that never moved. The floor is the blunt instrument — a minimum gap
    // that holds for the whole run whether or not a throttle has been seen yet.
    [Fact]
    public async Task MinRequestInterval_PacesEveryCall_EvenWithoutAThrottle()
    {
        var inner = new ThrottlingChatClient(failures: 0);
        var client = Wrap(inner, ("MinRequestIntervalMs", "300"));

        for (var i = 0; i < 4; i++)
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        for (var i = 1; i < inner.CallTimes.Count; i++)
        {
            var gap = inner.CallTimes[i] - inner.CallTimes[i - 1];
            Assert.True(gap >= TimeSpan.FromMilliseconds(250), $"call {i} came {gap.TotalMilliseconds:0}ms after the previous one");
        }
    }

    [Fact]
    public async Task MinRequestInterval_DoesNotDecayAfterSuccesses()
    {
        var inner = new ThrottlingChatClient(failures: 1);
        var client = Wrap(inner, ("MinRequestIntervalMs", "300"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "30"));

        for (var i = 0; i < 5; i++)
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var lastGap = inner.CallTimes[^1] - inner.CallTimes[^2];
        Assert.True(lastGap >= TimeSpan.FromMilliseconds(250), $"pace decayed below the floor (gap {lastGap.TotalMilliseconds:0}ms)");
    }

    [Fact]
    public async Task MinRequestInterval_AppliesWhenRetryIsDisabled()
    {
        var inner = new ThrottlingChatClient(failures: 0);
        var client = Wrap(inner, ("MaxRetries", "0"), ("MinRequestIntervalMs", "300"));

        Assert.NotSame(inner, client);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var gap = inner.CallTimes[1] - inner.CallTimes[0];
        Assert.True(gap >= TimeSpan.FromMilliseconds(250), $"gap {gap.TotalMilliseconds:0}ms");
    }
}

// ---------------------------------------------------------------------------
// bugs/Rate_Limit_Exhaustion_Hides_Its_Own_Cause.md
// ---------------------------------------------------------------------------

public class RetryBudgetAccountingTests
{
    private static IChatClient Wrap(IChatClient inner, params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return RetryingChatClient.Wrap(inner, config, config);
    }

    private sealed class AlwaysThrottling : IChatClient
    {
        public int Calls { get; private set; }
        private readonly string _message;

        public AlwaysThrottling(string message = "Wholesale rate limit exceeded for this gateway.")
            => _message = message;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException(_message);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    // The defect: PaceAsync ran inside the same stopwatch as the backoff, so a rising
    // pace ate the retry budget and the attempt count collapsed exactly when more
    // attempts were wanted. A run reported "attempt 8/10" having been stopped by a
    // budget spent almost entirely on pacing — and for an eval that is a provider's bad
    // afternoon recorded as a scoreable result about the model.
    //
    // The floor must exceed the backoff cap for this to discriminate at all, and that is
    // worth knowing about the mechanism: PaceAsync measures the gap from the *last
    // request*, and a backoff has already elapsed since then. So while backoff >= pace,
    // pacing costs nothing extra and charging it is harmless. It only eats the budget
    // once the pace outgrows the backoff — which is precisely the regime the reported run
    // was in, its pace pinned at the 60s cap while backoff delays ran shorter.
    //
    // Hence a 3s floor against a 1s backoff cap: each retry waits 0.5-1s of backoff and
    // then ~2s more of pacing. Uncharged, the budget check before the third retry sees
    // two backoffs (at most 2s) plus the next delay (at most 1s) against a 6s budget, so
    // the attempt cap binds at 4 calls with 3s to spare for a loaded runner's Task.Delay
    // overshoot. Charged, each retry costs the full 3s of pace, so the check before the
    // third retry sees 6s spent plus a delay and the budget runs out at 3 calls. (A 3s
    // budget with a 2s floor discriminated too, with no margin at all: CI dropped an
    // attempt on the first run that exercised it.)
    [Fact]
    public async Task PacingIsNotChargedToTheRetryBudget()
    {
        var inner = new AlwaysThrottling();
        var client = Wrap(inner,
            ("MaxRetries", "3"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "6"),
            ("MinRequestIntervalMs", "3000"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(4, inner.Calls); // the initial call plus MaxRetries
    }

    // A provider that says "come back in an hour" used to have its hint clamped to
    // _maxDelay and retried on a schedule it had explicitly rejected — 300 seconds and
    // 36 requests against a daily quota whose reset was hours away. The hint is now
    // compared to the budget before clamping.
    [Fact]
    public async Task AHintLongerThanTheBudgetStopsImmediately()
    {
        var inner = new AlwaysThrottling("Rate limit reached. Please try again in 3600 seconds.");
        var client = Wrap(inner,
            ("MaxRetries", "10"), ("RetryMaxDelaySeconds", "1"), ("RetryBudgetSeconds", "30"),
            ("MinRequestIntervalMs", "0"));

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(1, inner.Calls);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"waited {elapsed.TotalSeconds:0.#}s before giving up");
    }

    // provider_ms: the wrapper is the one place that sees inference, pacing and backoff,
    // so it is installed even with retry and pacing off — an unwrapped client would
    // report 0 rather than "unmeasured".
    [Fact]
    public async Task ProviderTimeIsChargedEvenWithRetryDisabled()
    {
        var clock = new ProviderTime();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MaxRetries"] = "0" })
            .Build();
        var inner = new SlowClient(TimeSpan.FromMilliseconds(200));
        var client = RetryingChatClient.Wrap(inner, config, config, clock);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.True(clock.Total >= TimeSpan.FromMilliseconds(150),
            $"provider time {clock.Total.TotalMilliseconds:0}ms did not capture a 200ms call");
    }

    // Backoff and pacing are provider time too — the consumer's question is "how long was
    // this run blocked on a provider", and a retry ladder is time the run was blocked.
    [Fact]
    public async Task ProviderTimeIncludesBackoffAndPacing()
    {
        var clock = new ProviderTime();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MaxRetries"] = "2",
                ["RetryMaxDelaySeconds"] = "1",
                ["RetryBudgetSeconds"] = "30",
                ["MinRequestIntervalMs"] = "300",
            }).Build();
        var inner = new AlwaysThrottling();
        var client = RetryingChatClient.Wrap(inner, config, config, clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        // Three calls, each preceded by a >=300ms pace, plus two backoffs.
        Assert.True(clock.Total >= TimeSpan.FromMilliseconds(600),
            $"provider time {clock.Total.TotalMilliseconds:0}ms did not include the waits");
    }

    private sealed class SlowClient(TimeSpan delay) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
