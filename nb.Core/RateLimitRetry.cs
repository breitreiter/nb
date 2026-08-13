using System.Net;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb.Utilities;
using Spectre.Console;

namespace nb;

/// <summary>
/// Recognizes a provider rate-limit rejection from an arbitrary provider SDK's
/// exception, without nb.Core referencing any of those SDKs.
///
/// Every provider signals the same condition differently — an HTTP 429, an SDK
/// exception carrying a <c>Status</c>, or (behind a gateway) prose in the message
/// with no usable status at all. Cloudflare's AI Gateway, for instance, rejects with
/// "Wholesale rate limit exceeded for this gateway. Please reduce request rate".
/// So the check is deliberately layered: status first, message text as the fallback.
///
/// False positives cost a retry; false negatives kill a run 40 turns deep. The
/// signal list is tuned accordingly — broad, but each entry is a phrase that only
/// appears in a throttling rejection.
/// </summary>
internal static class RateLimitClassifier
{
    private static readonly string[] Signals =
    {
        "rate limit",
        "rate_limit",
        "ratelimit",
        "too many requests",
        "reduce request rate",
        "overloaded",
        "quota exceeded",
        "exceeded your current quota",
    };

    // A bare "429" in prose, not part of a longer number (token counts, ids).
    private static readonly Regex StatusInText = new(@"(?<![0-9])429(?![0-9])", RegexOptions.Compiled);

    // Providers that bother to say when to come back usually say it in the message
    // body; the header is long gone by the time an SDK exception reaches us.
    private static readonly Regex RetryAfterInText = new(
        @"(?:retry[- ]?after|try again in)\D{0,4}(\d+(?:\.\d+)?)\s*(ms|milliseconds?|s|secs?|seconds?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsRateLimit(Exception exception, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        if (exception is OperationCanceledException) return false;

        for (var ex = exception; ex != null; ex = ex.InnerException)
        {
            if (IsThrottleStatus(StatusOf(ex)) || StatusInText.IsMatch(ex.Message) || MatchesSignal(ex.Message))
            {
                retryAfter = ParseRetryAfter(ex.Message);
                return true;
            }
        }
        return false;
    }

    private static bool IsThrottleStatus(int? status) => status is 429 or 503 or 529;

    private static bool MatchesSignal(string message) =>
        !string.IsNullOrEmpty(message)
        && Signals.Any(s => message.Contains(s, StringComparison.OrdinalIgnoreCase));

    // HttpRequestException exposes StatusCode; System.ClientModel's
    // ClientResultException (OpenAI/Azure SDKs) exposes Status. Read whichever is
    // there — reflection keeps nb.Core free of provider SDK references, which the
    // AssemblyLoadContext isolation requires anyway.
    private static int? StatusOf(Exception ex)
    {
        if (ex is HttpRequestException http)
            return http.StatusCode is { } code ? (int)code : null;

        var property = ex.GetType().GetProperty("Status") ?? ex.GetType().GetProperty("StatusCode");
        return property?.GetValue(ex) switch
        {
            int i => i,
            HttpStatusCode c => (int)c,
            _ => null,
        };
    }

    private static TimeSpan? ParseRetryAfter(string message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        var match = RetryAfterInText.Match(message);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) return null;

        var unit = match.Groups[2].Value;
        return unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase) || unit.StartsWith("milli", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromMilliseconds(value)
            : TimeSpan.FromSeconds(value);
    }
}

/// <summary>
/// Wraps the provider's <see cref="IChatClient"/> and retries a rate-limited call
/// with exponential backoff + jitter, honoring a retry-after hint when the provider
/// gives one. Without this a single 429 anywhere in an agentic run throws the whole
/// run away (exit 2) along with every tool call it had already made.
///
/// **Streaming is only retried before the first update is yielded.** nb renders
/// prose incrementally as it arrives, so re-running a call that already emitted text
/// would duplicate it on screen and in the transcript. That's not a real limitation:
/// throttling happens at request admission, before the first token.
///
/// When the retries run out the exception propagates unchanged —
/// <see cref="ConversationManager"/> re-classifies it into
/// <c>exit_reason rate_limited</c> so a harness can tell "back off and re-run me"
/// from "your program is broken".
/// </summary>
internal sealed class RetryingChatClient : DelegatingChatClient
{
    private const int DefaultMaxRetries = 5;
    private const int DefaultMaxDelaySeconds = 60;
    private const double BaseDelayMs = 1000;

    private readonly int _maxRetries;
    private readonly TimeSpan _maxDelay;

    private RetryingChatClient(IChatClient inner, int maxRetries, TimeSpan maxDelay) : base(inner)
    {
        _maxRetries = maxRetries;
        _maxDelay = maxDelay;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> using the active entry's <c>MaxRetries</c> /
    /// <c>RetryMaxDelaySeconds</c>, falling back to the root config then to the
    /// defaults. <c>MaxRetries: 0</c> opts out and returns the client untouched.
    /// </summary>
    public static IChatClient Wrap(IChatClient inner, IConfiguration root, IConfiguration entry)
    {
        var maxRetries = ReadInt(entry["MaxRetries"]) ?? ReadInt(root["MaxRetries"]) ?? DefaultMaxRetries;
        if (maxRetries <= 0) return inner;

        var maxDelay = ReadInt(entry["RetryMaxDelaySeconds"]) ?? ReadInt(root["RetryMaxDelaySeconds"]) ?? DefaultMaxDelaySeconds;
        return new RetryingChatClient(inner, maxRetries, TimeSpan.FromSeconds(Math.Max(1, maxDelay)));
    }

    private static int? ReadInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(messages, options, cancellationToken);
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt, out var hint))
            {
                await WaitAsync(hint, attempt, cancellationToken);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            var yielded = false;
            var retrying = false;
            TimeSpan? hint = null;

            try
            {
                while (true)
                {
                    ChatResponseUpdate? update = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) yield break;
                        update = enumerator.Current;
                    }
                    catch (Exception ex) when (!yielded && ShouldRetry(ex, attempt, out var suggested))
                    {
                        hint = suggested;
                        retrying = true;
                    }

                    if (retrying) break;
                    yielded = true;
                    yield return update!;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            await WaitAsync(hint, attempt, cancellationToken);
        }
    }

    private bool ShouldRetry(Exception ex, int attempt, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        return attempt < _maxRetries && RateLimitClassifier.IsRateLimit(ex, out retryAfter);
    }

    private async Task WaitAsync(TimeSpan? hint, int attempt, CancellationToken cancellationToken)
    {
        var delay = ComputeDelay(hint, attempt);
        AnsiConsole.MarkupLine(
            $"[{UIColors.SpectreWarning}]⚠ Rate limited by provider; retrying in {delay.TotalSeconds:0.#}s "
            + $"(attempt {attempt + 1}/{_maxRetries})[/]");

        // Honors the run's wall-clock budget / Ctrl-C: a long backoff must stay cancellable.
        await Task.Delay(delay, cancellationToken);
    }

    // Half-jitter: half the exponential window is fixed, half is random, so a fleet
    // of nb runs that all hit the same gateway limit don't march back in lockstep.
    private TimeSpan ComputeDelay(TimeSpan? hint, int attempt)
    {
        if (hint is { } suggested && suggested > TimeSpan.Zero)
            return suggested < _maxDelay ? suggested : _maxDelay;

        var window = Math.Min(_maxDelay.TotalMilliseconds, BaseDelayMs * Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(window / 2 + Random.Shared.NextDouble() * window / 2);
    }
}
