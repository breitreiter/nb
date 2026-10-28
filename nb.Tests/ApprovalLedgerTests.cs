using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The approval disposition an emitted transcript carries — `approval-is-not-a-boundary`
/// work item 0, the prerequisite for plans/approval-without-prompts.md.
///
/// A message does not carry approval disposition (see <see cref="TranscriptMapper"/>), so the
/// ledger stamps each live decision on its journaled call as it is made. These tests pin
/// that channel: with no decision the call is unstamped, and with one the verdict and the
/// deciding ladder rung both survive to the wire.
/// </summary>
public class ApprovalLedgerTests
{
    private static List<ChatMessage> TwoCalls() => new()
    {
        new ChatMessage(ChatRole.User, "do two things"),
        new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "bash", new Dictionary<string, object?> { ["command"] = "git status" }),
            new FunctionCallContent("call-2", "write_file", new Dictionary<string, object?> { ["path"] = "/etc/motd" }),
        }),
    };

    [Fact]
    public void NoLedger_LeavesEnrichmentAbsent()
    {
        var events = TranscriptMapper.FromMessages(TwoCalls());

        foreach (var call in events.OfType<ToolCallEvent>())
        {
            Assert.Null(call.Approved);
            Assert.Null(call.ApprovalReason);
        }
    }

    private static RunJournal Journaled(IEnumerable<ChatMessage> messages)
    {
        var journal = new RunJournal();
        foreach (var m in messages) journal.Append(m);
        return journal;
    }

    [Fact]
    public void Ledger_StampsVerdictAndReasonOntoMatchingCall()
    {
        var journal = Journaled(TwoCalls());
        var ledger = new ApprovalLedger(journal);
        ledger.RecordAllow("call-1", ApprovalLedger.PreApproved);
        ledger.RecordDeny("call-2", ApprovalLedger.DefaultDeny);

        var calls = journal.Events.OfType<ToolCallEvent>().ToList();

        Assert.Equal(ApprovalLedger.Allow, calls[0].Approved);
        Assert.Equal(ApprovalLedger.PreApproved, calls[0].ApprovalReason);
        Assert.Equal(ApprovalLedger.Deny, calls[1].Approved);
        Assert.Equal(ApprovalLedger.DefaultDeny, calls[1].ApprovalReason);
    }

    [Fact]
    public void Ledger_LeavesUnrecordedCallsUnstamped()
    {
        var journal = Journaled(TwoCalls());
        new ApprovalLedger(journal).RecordAllow("call-1", ApprovalLedger.Trust);

        var calls = journal.Events.OfType<ToolCallEvent>().ToList();

        Assert.Equal(ApprovalLedger.Allow, calls[0].Approved);
        Assert.Null(calls[1].Approved); // never reached the policy — not silently an allow
    }

    // A provider that reuses a call id across rounds (the Mock does) used to get the last
    // round's verdict stamped on every call of that id; the decision now lands on the call
    // it decided.
    [Fact]
    public void ReusedCallId_StampsOnlyTheCallJustDecided()
    {
        var round = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("same", "bash")]);
        var journal = Journaled([new ChatMessage(ChatRole.User, "go"), round]);
        var ledger = new ApprovalLedger(journal);
        ledger.RecordAllow("same", ApprovalLedger.Safe);
        journal.Append(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("same", "bash")]));
        ledger.RecordDeny("same", ApprovalLedger.NoMatch);

        var calls = journal.Events.OfType<ToolCallEvent>().ToList();
        Assert.Equal(ApprovalLedger.Allow, calls[0].Approved);
        Assert.Equal(ApprovalLedger.Deny, calls[1].Approved);
    }

    [Fact]
    public void DeniedCount_CountsOnlyRefusals()
    {
        var ledger = new ApprovalLedger();
        ledger.RecordAllow("a", ApprovalLedger.Safe);
        ledger.RecordDeny("b", ApprovalLedger.DefaultDeny);
        ledger.RecordDeny("c", ApprovalLedger.NoMatch);

        Assert.Equal(2, ledger.DeniedCount);
    }

    [Fact]
    public void Trailer_OmitsDeniedWhenZero_SoACleanRunStaysByteIdentical()
    {
        var events = TranscriptMapper.FromMessages(TwoCalls());
        var trailer = TranscriptMapper.ResultTrailer(events, deniedCount: 0);

        Assert.Null(trailer.Denied);
        Assert.DoesNotContain("denied", TranscriptSerializer.SerializeEvent(trailer));
    }

    [Fact]
    public void Trailer_CarriesDenialCount_AndRoundTrips()
    {
        var events = TranscriptMapper.FromMessages(TwoCalls());
        var trailer = TranscriptMapper.ResultTrailer(events, deniedCount: 3);

        Assert.Equal(3, trailer.Denied);

        var line = TranscriptSerializer.SerializeEvent(trailer);
        var back = Assert.IsType<ResultEvent>(TranscriptSerializer.ParseLine(line, 1));
        Assert.Equal(3, back.Denied);
    }

    [Fact]
    public void ToolCallApproval_RoundTripsThroughTheWire()
    {
        var ev = new ToolCallEvent
        {
            Turn = 1,
            Id = "call-1",
            Name = "bash",
            Approved = ApprovalLedger.Deny,
            ApprovalReason = ApprovalLedger.DefaultDeny,
        };

        var back = Assert.IsType<ToolCallEvent>(TranscriptSerializer.ParseLine(TranscriptSerializer.SerializeEvent(ev), 1));

        Assert.Equal(ApprovalLedger.Deny, back.Approved);
        Assert.Equal(ApprovalLedger.DefaultDeny, back.ApprovalReason);
    }
}
