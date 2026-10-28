using nb.Transcript;

namespace nb.Shell;

/// <summary>
/// What the approval policy decided, keyed by tool-call id. Decisions are made deep in the
/// harness at execution time; each one is stamped on its <c>tool_call</c> in the run's
/// <see cref="RunJournal"/> as it is made, and kept here for the trailer's denial count and
/// the error tracker's "was that a denial?".
///
/// Not a policy component: it decides nothing.
/// </summary>
public sealed class ApprovalLedger(RunJournal? journal = null)
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

    public void RecordAllow(string callId, string reason) => Record(callId, Allow, reason);

    public void RecordDeny(string callId, string reason) => Record(callId, Deny, reason);

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

    private void Record(string callId, string verdict, string reason)
    {
        _entries[callId] = (verdict, reason);
        journal?.RecordApproval(callId, verdict, reason);
    }
}
