namespace nb.Shell;

/// <summary>
/// The live record of what the approval policy decided, keyed by tool-call id.
///
/// It exists because the transcript's tool_call events are mapped post-hoc from chat
/// history, and history carries no approval disposition — see
/// <c>TranscriptMapper.FromHistory</c>. Decisions are made deep in the harness at
/// execution time and would otherwise be discarded; this collects them so the emit
/// step can stamp them on. Same route usage takes to the trailer.
///
/// Not a policy component: it decides nothing and is not consulted. Write-only during a
/// run, read once at emit.
/// </summary>
public sealed class ApprovalLedger
{
    private readonly Dictionary<string, (string Verdict, string? Reason)> _entries = new();

    public const string Allow = "allow";
    public const string Deny = "deny";

    // Ladder rungs, in the order DecideBash consults them.
    public const string PreApproved = "pre-approved";
    public const string Safe = "safe";
    public const string Trust = "trust";
    public const string DefaultDeny = "default-deny";
    public const string NoMatch = "no-match";

    public void RecordAllow(string callId, string reason) => _entries[callId] = (Allow, reason);

    public void RecordDeny(string callId, string reason) => _entries[callId] = (Deny, reason);

    public int DeniedCount => _entries.Values.Count(e => e.Verdict == Deny);

    public bool TryGet(string callId, out string verdict, out string? reason)
    {
        if (_entries.TryGetValue(callId, out var entry))
        {
            (verdict, reason) = entry;
            return true;
        }

        (verdict, reason) = (Allow, null);
        return false;
    }
}
