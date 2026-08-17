using System.Diagnostics;
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
            var text = TextOf(ex);
            if (IsThrottleStatus(StatusOf(ex)) || StatusInText.IsMatch(text) || MatchesSignal(text))
            {
                retryAfter = ParseRetryAfter(text);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Everything the exception can tell us in prose: its message, plus the raw HTTP
    /// response body when the SDK kept one.
    ///
    /// The body is not optional detail — it is the only place the decisive text lives.
    /// System.ClientModel's <c>ClientResultException</c> (OpenAI/Azure SDKs) formats
    /// its message as just "Service request failed.\nStatus: 402 (Payment Required)"
    /// and leaves the server's explanation on <c>GetRawResponse().Content</c>. A
    /// gateway's "Wholesale rate limit exceeded... reduce request rate" therefore never
    /// appears in <see cref="Exception.Message"/>, and a classifier reading only the
    /// message retries nothing at all.
    /// </summary>
    private static string TextOf(Exception ex)
    {
        var body = ResponseBodyOf(ex);
        return string.IsNullOrEmpty(body) ? ex.Message : ex.Message + "\n" + body;
    }

    // Reflection again, for the same reason as StatusOf: the SDK types live behind an
    // AssemblyLoadContext boundary that nb.Core must not reference. The error response
    // is buffered by the time the exception is constructed, so reading Content here
    // does not consume a live stream — but anything unexpected is swallowed, since a
    // failed peek must degrade to "no extra text", never to a second exception thrown
    // while classifying the first.
    private static string? ResponseBodyOf(Exception ex)
    {
        try
        {
            var raw = ex.GetType().GetMethod("GetRawResponse", Type.EmptyTypes)?.Invoke(ex, null);
            return raw?.GetType().GetProperty("Content")?.GetValue(raw)?.ToString();
        }
        catch
        {
            return null;
        }
    }

    // 402 is deliberately absent. Cloudflare's gateway signals wholesale capacity
    // exhaustion with it ("Wholesale rate limit exceeded... reduce request rate"),
    // which must retry — but 402 is Payment Required, and an unfunded account would
    // burn the whole backoff budget before failing anyway. So a 402 retries on the
    // strength of its body, via the prose layer (see TextOf), and not on the status alone.
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
/// When the retry budget runs out the exception propagates unchanged —
/// <see cref="ConversationManager"/> re-classifies it into
/// <c>exit_reason rate_limited</c> so a harness can tell "back off and re-run me"
/// from "your program is broken".
///
/// **The budget is wall-clock, not just attempts.** A gateway-wide capacity limit
/// lasts minutes; a plain 5-attempt exponential ladder gives up after ~30 seconds,
/// which is not a real attempt to outlast it. Retrying continues while both the
/// attempt cap and the time budget hold.
///
/// **A throttle also slows the requests that follow it** (see <see cref="PaceAsync"/>).
/// Retrying only the failed call means a long agentic run charges back in at full
/// rate the moment one call succeeds, and rediscovers the same limit turn after turn
/// — paying for each rediscovery.
/// </summary>
internal sealed class RetryingChatClient : DelegatingChatClient
{
    private const int DefaultMaxRetries = 10;
    private const int DefaultMaxDelaySeconds = 60;
    private const int DefaultBudgetSeconds = 300;
    private const double BaseDelayMs = 1000;

    // Where pacing starts after the first throttle, and the point below which a
    // decayed pace is close enough to zero to stop bothering.
    private static readonly TimeSpan InitialPace = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumPace = TimeSpan.FromMilliseconds(250);

    private readonly int _maxRetries;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _budget;

    private readonly SemaphoreSlim _paceGate = new(1, 1);
    private TimeSpan _pace = TimeSpan.Zero;
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    private RetryingChatClient(IChatClient inner, int maxRetries, TimeSpan maxDelay, TimeSpan budget) : base(inner)
    {
        _maxRetries = maxRetries;
        _maxDelay = maxDelay;
        _budget = budget;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> using the active entry's <c>MaxRetries</c> /
    /// <c>RetryMaxDelaySeconds</c> / <c>RetryBudgetSeconds</c>, falling back to the
    /// root config then to the defaults. <c>MaxRetries: 0</c> opts out and returns the
    /// client untouched.
    /// </summary>
    public static IChatClient Wrap(IChatClient inner, IConfiguration root, IConfiguration entry)
    {
        var maxRetries = ReadInt(entry["MaxRetries"]) ?? ReadInt(root["MaxRetries"]) ?? DefaultMaxRetries;
        if (maxRetries <= 0) return inner;

        var maxDelay = ReadInt(entry["RetryMaxDelaySeconds"]) ?? ReadInt(root["RetryMaxDelaySeconds"]) ?? DefaultMaxDelaySeconds;
        var budget = ReadInt(entry["RetryBudgetSeconds"]) ?? ReadInt(root["RetryBudgetSeconds"]) ?? DefaultBudgetSeconds;
        return new RetryingChatClient(inner, maxRetries,
            TimeSpan.FromSeconds(Math.Max(1, maxDelay)), TimeSpan.FromSeconds(Math.Max(1, budget)));
    }

    private static int? ReadInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var spent = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(cancellationToken);
            try
            {
                var response = await base.GetResponseAsync(messages, options, cancellationToken);
                OnSucceeded();
                return response;
            }
            catch (Exception ex) when (ShouldRetry(ex, attempt, spent.Elapsed, out var delay))
            {
                await OnThrottledAsync(delay, attempt, cancellationToken);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var spent = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(cancellationToken);

            var enumerator = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);

            var yielded = false;
            var retrying = false;
            var completed = false;
            var delay = TimeSpan.Zero;

            try
            {
                while (true)
                {
                    ChatResponseUpdate? update = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync()) { completed = true; }
                        else update = enumerator.Current;
                    }
                    catch (Exception ex) when (!yielded && ShouldRetry(ex, attempt, spent.Elapsed, out var suggested))
                    {
                        delay = suggested;
                        retrying = true;
                    }

                    if (retrying || completed) break;
                    yielded = true;
                    yield return update!;
                }
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            if (completed)
            {
                OnSucceeded();
                yield break;
            }

            await OnThrottledAsync(delay, attempt, cancellationToken);
        }
    }

    private bool ShouldRetry(Exception ex, int attempt, TimeSpan spent, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (attempt >= _maxRetries) return false;
        if (!RateLimitClassifier.IsRateLimit(ex, out var hint)) return false;

        delay = ComputeDelay(hint, attempt);

        // Don't start a wait the budget can't cover — sleeping past the budget only
        // to fail anyway wastes the caller's wall clock without buying an attempt.
        return spent + delay <= _budget;
    }

    private async Task OnThrottledAsync(TimeSpan delay, int attempt, CancellationToken cancellationToken)
    {
        RaisePace();
        AnsiConsole.MarkupLine(
            $"[{UIColors.SpectreWarning}]⚠ Rate limited by provider; retrying in {delay.TotalSeconds:0.#}s "
            + $"(attempt {attempt + 1}/{_maxRetries})[/]");

        // Honors the run's wall-clock budget / Ctrl-C: a long backoff must stay cancellable.
        await Task.Delay(delay, cancellationToken);
    }

    /// <summary>
    /// Holds the configured minimum gap between requests once a throttle has been seen.
    /// Zero until the first one, so an unthrottled run pays nothing for this.
    /// </summary>
    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        await _paceGate.WaitAsync(cancellationToken);
        TimeSpan wait;
        try
        {
            wait = _pace <= TimeSpan.Zero
                ? TimeSpan.Zero
                : _lastRequest + _pace - DateTimeOffset.UtcNow;
            // Claim the slot before releasing, so concurrent callers (a library host
            // running several conversations through one client) queue instead of all
            // measuring against the same stale timestamp.
            _lastRequest = DateTimeOffset.UtcNow + (wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
        }
        finally
        {
            _paceGate.Release();
        }

        if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
    }

    // A throttle doubles the pace (from a 1s floor, capped at the single-backoff cap);
    // each clean response halves it back toward zero. Fast to slow down, slow to speed
    // up — the standard shape, because the cost of being too fast is a failed run and
    // the cost of being too slow is a few seconds. The pace is deliberately a gentle
    // drag rather than a copy of the backoff delay: the backoff already covers the
    // immediate wait, this only keeps the following turns from charging back in.
    private void RaisePace()
    {
        var raised = _pace <= TimeSpan.Zero ? InitialPace : _pace * 2;
        _pace = raised > _maxDelay ? _maxDelay : raised;
    }

    private void OnSucceeded()
    {
        if (_pace <= TimeSpan.Zero) return;
        var decayed = _pace / 2;
        _pace = decayed < MinimumPace ? TimeSpan.Zero : decayed;
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
