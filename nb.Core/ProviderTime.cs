using System.Diagnostics;

namespace nb;

/// <summary>
/// Wall-clock time a run spent blocked on a provider — inference, pacing, backoff and
/// every retry, as one number.
/// </summary>
/// <remarks>
/// Deliberately not a breakdown. A consumer reading a transcript cannot act on *why* a
/// provider was slow: a model that reasons for ninety seconds and a gateway having a bad
/// afternoon are the same non-actionable fact. What they can act on is the complement —
/// <c>duration_ms - provider_ms</c> is the run's own work, where a slow tool call or an
/// expensive query actually shows up. So the split that earns its place is
/// provider-versus-everything-else, and finer attribution would be noise with a
/// maintenance cost.
///
/// Scoped to a <see cref="Providers.ProviderManager"/>, which is built once per run.
/// Every client it hands out shares one accumulator, so a mid-program provider swap and
/// the oracle's side call land in the same total without threading a parameter through
/// the evaluator. Adds are interlocked because a library host can drive several
/// conversations through one runtime.
/// </remarks>
public sealed class ProviderTime
{
    private long _ticks;

    public void Add(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);

    public TimeSpan Total => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));

    /// <summary>Times <paramref name="work"/> and charges it, however it exits.</summary>
    public async Task<T> MeasureAsync<T>(Func<Task<T>> work)
    {
        var started = Stopwatch.GetTimestamp();
        try { return await work(); }
        finally { Add(Stopwatch.GetElapsedTime(started)); }
    }
}
