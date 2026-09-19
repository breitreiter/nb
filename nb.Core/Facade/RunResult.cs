using nb.Transcript;

namespace nb;

/// <summary>
/// The result of one <see cref="Nb.RunAsync(Microsoft.Extensions.Configuration.IConfiguration, System.Collections.Generic.IReadOnlyList{TranscriptEvent}, NbOptions?, System.Threading.CancellationToken)"/>
/// — isomorphic to the published transcript contract, nothing more. No engine types
/// cross this boundary: <see cref="Events"/> is the JSONL schema as records,
/// <see cref="ExitReason"/> is the exit-code vocabulary, <see cref="Usage"/> is the
/// token trailer. See plans/composable-cli-reorientation.md (Pillar 5).
/// </summary>
public sealed record RunResult
{
    /// <summary>The completed conversation as transcript events (no trailer).</summary>
    public required IReadOnlyList<TranscriptEvent> Events { get; init; }

    /// <summary>The model's final prose — the last non-empty assistant-text event.</summary>
    public required string Answer { get; init; }

    /// <summary>Token usage summed across every run in the program, or null if unreported.</summary>
    public UsageInfo? Usage { get; init; }

    /// <summary>The fine-grained exit reason (<see cref="ExitReasons"/> vocabulary).</summary>
    public required string ExitReason { get; init; }

    /// <summary>The coarse process exit code the reason collapses to.</summary>
    public required int ExitCode { get; init; }

    /// <summary>
    /// The provider entry that actually answered — the effective one, not the one the
    /// program requested. Mirrors <c>provider</c> on the transcript trailer, and exists
    /// so a corpus of runs cannot be mis-attributed to a provider that never ran. See
    /// bugs/Failed_Provider_Directive_Silently_Substitutes.md.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// The harness the run wore, when it was not nb's own — null for the default.
    /// Mirrors the <c>harness</c> field on the transcript trailer.
    /// </summary>
    public string? Harness { get; init; }

    /// <summary>
    /// How many tool calls the approval policy refused. Mirrors <c>denied</c> on the
    /// transcript trailer. A library host branching on this does not have to walk
    /// <see cref="Events"/> to learn the run was fighting its authorization envelope.
    /// </summary>
    public int Denied { get; init; }

    /// <summary>
    /// How many times the oracle resolved a halt and continued the run. Mirrors
    /// <c>oracle_turns</c> on the transcript trailer; zero without an <c>oracle</c>.
    /// </summary>
    public int OracleTurns { get; init; }

    /// <summary>The oracle's last raw verdict, as the judge wrote it; null if no oracle was consulted. Mirrors <c>oracle_verdict</c> on the trailer.</summary>
    public string? OracleVerdict { get; init; }

    /// <summary>Non-fatal evaluator warnings (unknown directive value, unbuildable client, …).</summary>
    /// <summary>
    /// The model that actually answered — the effective one, not the one the program
    /// requested. Read off the live client, so it is downstream of every fallback: the
    /// <c>model</c> directive, the entry's <c>Model</c> field, and the plugin's own
    /// hard-coded default. Exists so a corpus of runs sweeping model names cannot be
    /// mis-attributed to a model that never ran.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// What the run cost in USD, or null when no provider entry declared a price.
    /// Inherits <see cref="UsageInfo.Estimated"/>: when usage is nb's size estimate the
    /// cost is estimated too, and there is deliberately no second flag for that.
    /// </summary>
    public double? Cost { get; init; }

    /// <summary>SHA-256 of the resolved program (the serialized event list, includes
    /// expanded and any seed spliced in) — what ran, rather than the source that
    /// described it.</summary>
    public string? ProgramSha256 { get; init; }

    /// <summary>nb.Core's informational version — the engine that ran. A library host's
    /// own version is its own business.</summary>
    public string? NbVersion { get; init; }

    /// <summary>
    /// Wall-clock time evaluating the program — model calls, tool calls and all. Excludes
    /// engine startup (config, provider discovery, MCP connect), which is nb's cost, not
    /// the program's.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Of <see cref="Duration"/>, how much was spent blocked on a provider: inference,
    /// pacing, backoff, every retry, and the oracle's side call.
    /// </summary>
    /// <remarks>
    /// The number worth reading is the difference. <c>Duration - ProviderTime</c> is the
    /// run's own work, which is where a slow tool or an expensive query shows up and is
    /// the only part anyone can act on. A <see cref="ProviderTime"/> close to
    /// <see cref="Duration"/> means the provider was slow, which is a fact about someone
    /// else's afternoon — worth being able to discount, not worth investigating.
    /// Deliberately one bucket and not a breakdown of *why* the provider was slow.
    /// </remarks>
    public TimeSpan ProviderTime { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
