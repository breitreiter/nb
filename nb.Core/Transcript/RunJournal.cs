using Microsoft.Extensions.AI;

namespace nb.Transcript;

/// <summary>
/// The run as it happened, written as it happens: every message that enters the model's
/// view is journaled at that moment, as transcript events, along with what history cannot
/// carry: when it was written, and the model round that produced it.
///
/// It replaces rebuilding the transcript from history after the run
/// (<c>plans/atif-and-acp.md</c> stage 2). History is what the model sees, and compaction
/// rewrites it; the journal is append-only, so a compacted run keeps the turns its
/// history lost. The JSONL emitter reads <see cref="Events"/>; per-round metrics and
/// timestamps are not serialized to JSONL.
///
/// Turn assignment: a counter advances once per message, except a tool message inherits
/// the turn of the assistant message it answers, so the loader can re-batch a round's
/// tool_calls and tool_results by shared turn.
/// </summary>
public sealed class RunJournal
{
    private readonly List<JournalEntry> _entries = new();
    private int _turn;
    private bool _started;

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public List<TranscriptEvent> Events => _entries.Select(e => e.Event).ToList();

    /// <summary>
    /// Journal a message as it enters history. <paramref name="round"/> is the model round
    /// that produced it (assistant messages only; null for anything nb or the program
    /// authored). <paramref name="source"/> tags a user message nb injected (<c>loop</c>,
    /// <c>todo</c>); <paramref name="oracle"/> marks the oracle's answer.
    /// </summary>
    public void Append(ChatMessage message, RoundMetrics? round = null, string? source = null, OracleAnswer? oracle = null)
    {
        if (message.Role != ChatRole.Tool && _started) _turn++;
        _started = true;

        var at = DateTimeOffset.UtcNow;
        foreach (var e in TranscriptMapper.FromMessage(message, _turn))
        {
            var tagged = e switch
            {
                UserEvent u when oracle is not null => u with { Source = "oracle", Keys = oracle.Keys, Verdict = oracle.Verdict },
                UserEvent u when source is not null => u with { Source = source },
                _ => e,
            };
            _entries.Add(new JournalEntry(tagged, at, round));
        }
    }

    /// <summary>Stamp the approval decision on the call it decided, at the moment it is made.</summary>
    public void RecordApproval(string callId, string verdict, string? reason)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].Event is ToolCallEvent call && call.Id == callId)
            {
                _entries[i] = _entries[i] with { Event = call with { Approved = verdict, ApprovalReason = reason } };
                return;
            }
        }
    }
}

/// <summary>One journaled event, with when it was written and the round that produced it.</summary>
public sealed record JournalEntry(TranscriptEvent Event, DateTimeOffset At, RoundMetrics? Round);

/// <summary>
/// One model round-trip: its index in the run (from 0), its token counts, whether nb
/// estimated them because the provider reported none, and the model that answered.
/// </summary>
public sealed record RoundMetrics(int Index, long Input, long Output, long Total, bool Estimated, string? Model);
