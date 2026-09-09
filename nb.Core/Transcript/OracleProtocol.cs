namespace nb.Transcript;

/// <summary>
/// The wire vocabulary of the oracle side call — the small, separate model call that
/// decides whether a finished run was clearly waiting on the user, and if so which
/// answer-sheet entries resolve it. See plans/oracle-resolver.md.
///
/// This is a *protocol* rather than an abstraction: two assemblies have to agree on
/// these literals, and one of them (the Mock provider) is loaded through its own
/// <c>AssemblyLoadContext</c> and cannot reference this one. So <c>Providers/Mock</c>
/// carries its own copy of <see cref="Sentinel"/> and the verdict tokens, and each
/// side's comment names the other. Duplication is the honest encoding of a wire
/// contract across an ALC boundary; a shared type would be the thing that breaks
/// (see the Assembly Context Gotcha in CLAUDE.md).
/// </summary>
public static class OracleProtocol
{
    /// <summary>
    /// Opens the oracle call's prompt, and the contract is specifically that it is the
    /// <b>start of that call's last user message</b> — which is what the Mock provider
    /// keys on to tell an oracle call apart from the subject conversation it is judging.
    ///
    /// It does double duty. Because it is a short, stable, distinct prefix, the oracle
    /// call does not share a prompt prefix with the subject conversation and so cannot
    /// disturb the subject's provider-side prompt cache.
    ///
    /// Versioned: if the oracle prompt's shape changes enough that an old Mock rider or
    /// a recorded transcript would be misread, bump it rather than redefining it in place.
    /// </summary>
    public const string Sentinel = "[nb:oracle-resolver:1]";

    /// <summary>
    /// Opens the judged text — the assistant's last message — inside the oracle prompt,
    /// on its own line, AFTER the answer sheet. The Mock provider reads its scripted
    /// verdict only from what follows this marker. The sheet lives in the same prompt,
    /// and a sheet entry may legitimately carry Mock riders of its own (that is how a
    /// test chains one scripted question into the next), so a rider scan over the whole
    /// prompt would find the sheet's riders on every call and never reach DONE.
    /// </summary>
    public const string SubjectMarker = "[nb:oracle-resolver:1:subject]";

    /// <summary>The run is not clearly waiting on the user. Ends the run as <c>ok</c>.</summary>
    public const string Done = "DONE";

    /// <summary>
    /// The run clearly is waiting on the user, but nothing on the sheet applies. Ends the
    /// run as <see cref="ExitReasons.OracleMiss"/>.
    /// </summary>
    public const string Miss = "MISS";
}
