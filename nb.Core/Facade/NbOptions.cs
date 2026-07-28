namespace nb;

/// <summary>
/// Per-invocation knobs for <see cref="Nb.RunAsync(Microsoft.Extensions.Configuration.IConfiguration, System.Collections.Generic.IReadOnlyList{nb.Transcript.TranscriptEvent}, NbOptions?, System.Threading.CancellationToken)"/>.
/// These are the things the CLI would take as flags; everything else (provider,
/// model, approval policy, MCP servers) comes from the passed configuration and the
/// program itself. All optional — the defaults match a plain headless run.
/// </summary>
public sealed record NbOptions
{
    /// <summary>Working directory for shell/native tools. Defaults to the process cwd.</summary>
    public string Cwd { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Auto-approve non-dangerous tools within the working-directory sandbox.</summary>
    public bool Trust { get; init; }

    /// <summary>Expose no native shell/file tools (MCP-only isolation).</summary>
    public bool NoBash { get; init; }

    /// <summary>Verbose engine diagnostics (still routed to <see cref="DiagnosticsWriter"/>).</summary>
    public bool Verbose { get; init; }

    /// <summary>An explicit MCP manifest path; null uses the layered mcp.json resolution.</summary>
    public string? McpManifestPath { get; init; }

    /// <summary>
    /// Where the AI provider plugin DLLs live. A library host must set this (its own
    /// output dir has no <c>providers/</c>); null falls back to <c>config["ProvidersPath"]</c>,
    /// then the executable-relative <c>providers/</c> the CLI ships.
    /// </summary>
    public string? ProvidersDirectory { get; init; }

    /// <summary>Bash command patterns to auto-approve (the <c>--approve</c> equivalent).</summary>
    public IReadOnlyList<string> ApprovePatterns { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Doom-loop detector threshold. Null uses config (<c>DoomLoopThreshold</c>) or the
    /// default (3, enabled); a value of 0 or less disables the detector. A program's
    /// <c>loop</c> directive overrides this per run.
    /// </summary>
    public int? DoomLoopThreshold { get; init; }

    /// <summary>
    /// Session-cumulative token ceiling: the run aborts (<c>exit_reason token_budget</c>,
    /// exit code 3) once total usage crosses it. Null uses config (<c>TokenBudget</c>) or
    /// leaves it unlimited. A program's <c>budget tokens</c> directive overrides this.
    /// </summary>
    public long? TokenBudget { get; init; }

    /// <summary>
    /// Session-cumulative wall-clock ceiling in milliseconds: the run aborts
    /// (<c>exit_reason time_budget</c>, exit code 3) once elapsed time crosses it, cancelling
    /// the in-flight model call. Null uses config (<c>WallClockBudgetMs</c>) or leaves it
    /// unlimited. A program's <c>budget wall_ms</c> directive overrides this. (Independent of
    /// the <see cref="System.Threading.CancellationToken"/> passed to <c>RunAsync</c>, which a
    /// caller can also use to impose a deadline.)
    /// </summary>
    public long? WallClockBudgetMs { get; init; }

    /// <summary>Where engine chrome is written; null suppresses it (stdout stays clean).</summary>
    public TextWriter? DiagnosticsWriter { get; init; }
}
