using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using nb.Shell;

namespace nb.Transcript;

/// <summary>
/// Maps nb's in-memory conversation history (Microsoft.Extensions.AI
/// <see cref="ChatMessage"/>s) to the transcript event stream — the emit half of
/// S2. See plans/transcript-schema.md.
///
/// Produces the CORE events only. History carries no enrichment: approval
/// disposition, structured tool results, reasoning, and usage are instrumented
/// live during a run and thrown away, not stored on the message. A post-hoc emit
/// from history is therefore a valid, replayable transcript minus the
/// output-only superset — exactly the subset seed-load consumes.
///
/// Turn assignment: a counter advances once per message, except a tool message
/// inherits the turn of the assistant message it answers — so the loader can
/// re-batch a round's tool_calls and tool_results by shared turn.
/// </summary>
public static class TranscriptMapper
{
    /// <param name="approvals">
    /// Optional live-instrumented approval dispositions, keyed by call id. History does not
    /// carry them (see the type remarks), so they arrive by the same route as usage: passed
    /// in from the run rather than recovered from the messages. Omit it and the mapping is
    /// exactly what it was before — core events, no enrichment.
    /// </param>
    public static List<TranscriptEvent> FromHistory(IEnumerable<ChatMessage> history, ApprovalLedger? approvals = null)
    {
        var events = new List<TranscriptEvent>();
        int turn = 0;
        bool first = true;

        foreach (var msg in history)
        {
            bool isTool = msg.Role == ChatRole.Tool;
            if (!isTool && !first) turn++;
            first = false;

            if (isTool)
            {
                foreach (var r in msg.Contents.OfType<FunctionResultContent>())
                    events.Add(new ToolResultEvent { Turn = turn, Id = r.CallId, Output = ResultToOutput(r.Result) });
            }
            else if (msg.Role == ChatRole.System)
            {
                events.Add(new SystemEvent { Turn = turn, Text = ExtractText(msg) });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                var text = ExtractText(msg);
                if (!string.IsNullOrEmpty(text))
                    events.Add(new AssistantTextEvent { Turn = turn, Text = text });
                foreach (var call in msg.Contents.OfType<FunctionCallContent>())
                {
                    string? verdict = null, approvalReason = null;
                    if (approvals is not null && approvals.TryGet(call.CallId, out var v, out var r))
                        (verdict, approvalReason) = (v, r);

                    events.Add(new ToolCallEvent
                    {
                        Turn = turn,
                        Id = call.CallId,
                        Name = call.Name,
                        Arguments = ToJsonObject(call.Arguments),
                        Approved = verdict,
                        ApprovalReason = approvalReason,
                    });
                }
            }
            else
            {
                // User (and any unexpected role) — a plain message, multipart if it carries images.
                events.Add(BuildMessage(msg, turn));
            }
        }

        return events;
    }

    /// <summary>
    /// Build the run-level <see cref="ResultEvent"/> trailer. Counts are derived
    /// from the emitted events; usage is passed in from the live response (it is
    /// not in history).
    /// </summary>
    public static ResultEvent ResultTrailer(IReadOnlyList<TranscriptEvent> events, string exitReason = "ok", UsageInfo? usage = null, string? harness = null, int deniedCount = 0)
    {
        // "turns" = assistant rounds: distinct turns carrying an assistant message.
        // (Counting distinct turns rather than the max keeps the number meaningful
        // regardless of how many leading system/user messages shift the counter.)
        int turns = events
            .Where(e => e is AssistantTextEvent or ToolCallEvent)
            .Select(e => e.Turn)
            .Distinct()
            .Count();
        int toolCalls = events.OfType<ToolCallEvent>().Count();
        return new ResultEvent
        {
            ExitReason = exitReason,
            Usage = usage,
            Turns = turns,
            ToolCalls = toolCalls,
            Harness = harness,
            Denied = deniedCount > 0 ? deniedCount : null,
        };
    }

    /// <summary>The model's final prose: the last non-empty assistant-text event's
    /// text, or empty if the run produced none. Used for <see cref="nb.RunResult.Answer"/>
    /// — porcelain interleaves prose with TOOL/RESULT lines, so it can't serve this.</summary>
    public static string LastAnswer(IReadOnlyList<TranscriptEvent> events)
    {
        for (int i = events.Count - 1; i >= 0; i--)
            if (events[i] is AssistantTextEvent { Text: { Length: > 0 } t })
                return t;
        return "";
    }

    private static UserEvent BuildMessage(ChatMessage msg, int turn)
    {
        var hasImage = msg.Contents.OfType<DataContent>().Any();
        if (!hasImage)
            return new UserEvent { Turn = turn, Text = ExtractText(msg) };

        var parts = new List<ContentPart>();
        foreach (var c in msg.Contents)
        {
            switch (c)
            {
                case TextContent t when !string.IsNullOrEmpty(t.Text):
                    parts.Add(new TextPart { Text = t.Text });
                    break;
                case DataContent d:
                    // v1: image bytes don't round-trip; the note is the durable stand-in.
                    parts.Add(new ImagePart { MediaType = d.MediaType, Note = "[image]" });
                    break;
            }
        }
        return new UserEvent { Turn = turn, Content = parts };
    }

    private static string ExtractText(ChatMessage msg) =>
        string.Concat(msg.Contents.OfType<TextContent>().Select(t => t.Text));

    private static JsonObject? ToJsonObject(IDictionary<string, object?>? args)
    {
        if (args is null) return null;
        var obj = new JsonObject();
        foreach (var kvp in args)
            obj[kvp.Key] = kvp.Value is null ? null : JsonSerializer.SerializeToNode(kvp.Value);
        return obj;
    }

    private static string ResultToOutput(object? result) => result switch
    {
        null => "",
        string s => s,
        IEnumerable<AIContent> parts => string.Concat(parts.OfType<TextContent>().Select(t => t.Text)),
        _ => result.ToString() ?? "",
    };
}
