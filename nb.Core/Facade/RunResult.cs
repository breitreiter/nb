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

    /// <summary>The output mode an <c>output</c> directive requested, or null.</summary>
    public string? OutputMode { get; init; }

    /// <summary>Non-fatal evaluator warnings (unknown directive value, unbuildable client, …).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
