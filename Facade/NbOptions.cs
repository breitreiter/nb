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

    /// <summary>Bash command patterns to auto-approve (the <c>--approve</c> equivalent).</summary>
    public IReadOnlyList<string> ApprovePatterns { get; init; } = Array.Empty<string>();

    /// <summary>Where engine chrome is written; null suppresses it (stdout stays clean).</summary>
    public TextWriter? DiagnosticsWriter { get; init; }
}
