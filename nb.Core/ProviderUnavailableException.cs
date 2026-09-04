namespace nb;

/// <summary>
/// A <c>provider</c> (or <c>model</c>) directive named an entry whose chat client could
/// not be built — unknown entry, unloaded implementation, missing required keys, or a
/// throwing <c>CreateClient</c>.
///
/// Thrown rather than warned because a program that names a provider is asserting a
/// dependency, the same way <c>mcp +server</c> does; leaving the previous client live
/// answers the question with the wrong thing. Mirrors
/// <see cref="nb.Shell.SandboxUnavailableException"/> and
/// <c>McpServerUnavailableException</c>: the program path reports it and exits 1, while
/// the REPL catches it and keeps the session alive — a mistyped line there should not
/// tear down the session. See bugs/Failed_Provider_Directive_Silently_Substitutes.md.
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException(string message) : base(message) { }
}
