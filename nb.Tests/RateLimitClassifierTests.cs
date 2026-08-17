using System.Net;
using nb;

namespace nb.Tests;

// The classifier is the whole load-bearing guess: nb.Core can't reference any
// provider SDK, so a throttling rejection has to be recognized from a status it
// may not carry and prose it may not phrase consistently.
[Collection(ConsoleBoundCollection.Name)]
public class RateLimitClassifierTests
{
    private static bool IsRateLimit(Exception ex, out TimeSpan? retryAfter)
        => RateLimitClassifier.IsRateLimit(ex, out retryAfter);

    [Fact]
    public void Http429_IsRateLimit()
    {
        Assert.True(IsRateLimit(new HttpRequestException("nope", null, HttpStatusCode.TooManyRequests), out _));
    }

    [Fact]
    public void Http500_IsNotRateLimit()
    {
        Assert.False(IsRateLimit(new HttpRequestException("boom", null, HttpStatusCode.InternalServerError), out _));
    }

    // The failure that motivated this: Cloudflare's AI Gateway rejects wholesale
    // capacity with prose, and nothing else in the exception says "throttled".
    [Fact]
    public void CloudflareGatewayProse_IsRateLimit()
    {
        var ex = new InvalidOperationException(
            "Wholesale rate limit exceeded for this gateway. Please reduce request rate or use BYOK.");
        Assert.True(IsRateLimit(ex, out _));
    }

    [Fact]
    public void OverloadedProse_IsRateLimit()
    {
        Assert.True(IsRateLimit(new InvalidOperationException("{\"type\":\"overloaded_error\"}"), out _));
    }

    [Fact]
    public void NestedException_IsUnwrapped()
    {
        var inner = new HttpRequestException("throttled", null, HttpStatusCode.TooManyRequests);
        Assert.True(IsRateLimit(new InvalidOperationException("call failed", inner), out _));
    }

    [Fact]
    public void Cancellation_IsNeverRateLimit()
    {
        // A wall-clock budget cancel must reach RunAsync's classifier untouched,
        // and must never be retried into a hang.
        Assert.False(IsRateLimit(new OperationCanceledException("rate limit"), out _));
    }

    [Fact]
    public void OrdinaryModelError_IsNotRateLimit()
    {
        Assert.False(IsRateLimit(new InvalidOperationException("context length exceeded: 4291 tokens"), out _));
    }

    // An SDK exception exposing an int Status (System.ClientModel's shape) is read
    // reflectively, since nb.Core can't reference the type.
    private sealed class FakeClientResultException(int status, string message = "service error") : Exception(message)
    {
        public int Status { get; } = status;
    }

    [Fact]
    public void SdkStatusProperty_IsRead()
    {
        Assert.True(IsRateLimit(new FakeClientResultException(429), out _));
        Assert.False(IsRateLimit(new FakeClientResultException(400), out _));
    }

    // Cloudflare's gateway signals wholesale capacity exhaustion as 402, not 429.
    // 402 is deliberately NOT a throttle status on its own — it is Payment Required,
    // and retrying a genuinely unfunded account just burns the backoff budget before
    // failing anyway. It retries when the body corroborates it, and not otherwise.
    [Fact]
    public void Http402_WithThrottleProse_IsRateLimit()
    {
        var ex = new FakeClientResultException(402,
            "HTTP 402 (payment_required): {\"state\":\"Failed\",\"error\":\"Wholesale rate limit "
            + "exceeded for this gateway. Please reduce request rate or use BYOK.\"}");
        Assert.True(IsRateLimit(ex, out _));
    }

    [Fact]
    public void Http402_WithoutThrottleProse_IsNotRateLimit()
    {
        var ex = new FakeClientResultException(402,
            "HTTP 402 (payment_required): {\"error\":\"Insufficient credit. Add a payment method.\"}");
        Assert.False(IsRateLimit(ex, out _));
    }

    // The real System.ClientModel shape, and the reason a gateway 402 killed runs with
    // no retry at all: the SDK's message is *only* "Service request failed. Status:
    // 402 (Payment Required)". The server's explanation — the sole evidence that this
    // is throttling and not an unfunded account — lives on GetRawResponse().Content.
    // A fake whose message carries the body cannot catch this regression; this one can.
    private sealed class FakeSdkException(int status, string body)
        : Exception($"Service request failed.\nStatus: {status} (Payment Required)")
    {
        public int Status { get; } = status;
        public FakeRawResponse GetRawResponse() => new(body);
    }

    private sealed class FakeRawResponse(string body)
    {
        // BinaryData in the real SDK; the classifier only ever calls ToString on it.
        public object Content { get; } = body;
    }

    [Fact]
    public void SdkException_ThrottleProseOnlyInResponseBody_IsRateLimit()
    {
        var ex = new FakeSdkException(402,
            "{\"state\":\"Failed\",\"error\":\"Wholesale rate limit exceeded for this gateway. "
            + "Please reduce request rate or use BYOK.\"}");
        Assert.True(IsRateLimit(ex, out _));
    }

    [Fact]
    public void SdkException_PaymentProseInResponseBody_IsNotRateLimit()
    {
        var ex = new FakeSdkException(402,
            "{\"state\":\"Failed\",\"error\":\"Insufficient credit. Add a payment method.\"}");
        Assert.False(IsRateLimit(ex, out _));
    }

    [Fact]
    public void SdkException_RetryAfterHintInResponseBody_IsParsed()
    {
        var ex = new FakeSdkException(429, "{\"error\":\"rate limit; try again in 12 seconds\"}");
        Assert.True(IsRateLimit(ex, out var retryAfter));
        Assert.Equal(TimeSpan.FromSeconds(12), retryAfter);
    }

    // Reading the body is a reflective peek at an unknown type; a throwing accessor
    // must degrade to "no extra prose", not blow up the classification of the 429.
    private sealed class ThrowingRawResponseException() : Exception("rate limit exceeded")
    {
        public object GetRawResponse() => throw new InvalidOperationException("content already consumed");
    }

    [Fact]
    public void SdkException_ThrowingRawResponse_StillClassifiesFromMessage()
    {
        Assert.True(IsRateLimit(new ThrowingRawResponseException(), out _));
    }

    [Theory]
    [InlineData("Rate limit reached. Please try again in 3.2s", 3.2)]
    [InlineData("rate_limit_error; retry after 20 seconds", 20)]
    public void RetryAfterHint_IsParsedFromMessage(string message, double expectedSeconds)
    {
        Assert.True(IsRateLimit(new InvalidOperationException(message), out var retryAfter));
        Assert.NotNull(retryAfter);
        Assert.Equal(expectedSeconds, retryAfter!.Value.TotalSeconds, 2);
    }

    [Fact]
    public void RetryAfterHint_InMilliseconds_IsParsedAsMilliseconds()
    {
        Assert.True(IsRateLimit(new InvalidOperationException("rate limit; try again in 500ms"), out var retryAfter));
        Assert.Equal(TimeSpan.FromMilliseconds(500), retryAfter);
    }

    [Fact]
    public void NoHint_LeavesRetryAfterNull()
    {
        Assert.True(IsRateLimit(new InvalidOperationException("rate limit exceeded"), out var retryAfter));
        Assert.Null(retryAfter);
    }
}
