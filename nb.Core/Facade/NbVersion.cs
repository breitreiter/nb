using System.Reflection;

namespace nb;

/// <summary>
/// nb's own version, read once off the assembly that holds the engine.
/// </summary>
/// <remarks>
/// Deliberately nb.Core's and not the CLI's: <see cref="Nb.RunAsync"/> is what actually
/// ran, and a library host embedding the engine has its own version that is its own
/// business. One source for both the version flag and the result trailer's
/// <c>nb_version</c>, so the two cannot report different things about one binary.
///
/// The string carries the commit as build metadata — <c>0.9.0+7003c32…</c> — because the
/// SDK appends <c>SourceRevisionId</c> to the informational version on its own. Note it
/// names HEAD, not the working tree: a build with uncommitted edits reports a clean sha
/// that does not fully describe it. Standard for the mechanism, and worth knowing before
/// trusting a dev build's provenance.
/// </remarks>
public static class NbVersion
{
    /// <summary>The informational version, e.g. <c>0.9.0+7003c32…</c>.</summary>
    public static string Current { get; } =
        typeof(NbVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(NbVersion).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
