using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace nb.Transcript;

/// <summary>
/// Maps Microsoft.Extensions.AI <see cref="ChatMessage"/>s to transcript events, one
/// message at a time. <see cref="RunJournal"/> calls it as each message enters history;
/// see plans/transcript-schema.md for the events.
///
/// Produces the CORE events only: a message carries no approval disposition, injected
/// source, or usage. The journal adds those at the point they are decided.
/// </summary>
public static class TranscriptMapper
{
    /// <summary>
    /// A message list as events, turns assigned as the journal assigns them. For a list that
    /// is not a run (a seed reloaded, a test's fixture): a run's record is its journal.
    /// </summary>
    public static List<TranscriptEvent> FromMessages(IEnumerable<ChatMessage> messages)
    {
        var journal = new RunJournal();
        foreach (var msg in messages) journal.Append(msg);
        return journal.Events;
    }

    /// <summary>One message's events, all at <paramref name="turn"/>.</summary>
    public static IEnumerable<TranscriptEvent> FromMessage(ChatMessage msg, int turn)
    {
        if (msg.Role == ChatRole.Tool)
        {
            foreach (var r in msg.Contents.OfType<FunctionResultContent>())
                yield return new ToolResultEvent { Turn = turn, Id = r.CallId, Output = ResultToOutput(r.Result) };
        }
        else if (msg.Role == ChatRole.System)
        {
            yield return new SystemEvent { Turn = turn, Text = ExtractText(msg) };
        }
        else if (msg.Role == ChatRole.Assistant)
        {
            var text = ExtractText(msg);
            if (!string.IsNullOrEmpty(text))
            {
                yield return new AssistantTextEvent { Turn = turn, Text = text };

                // Enrichment beside the canonical text event: the parsed trailing
                // ```json fence, so a consumer wanting the structured answer does not
                // re-parse it. bugs/Assistant_Json_Event_Is_Never_Emitted.md
                if (ParseTrailingJsonFence(text) is { } node)
                    yield return new AssistantJsonEvent { Turn = turn, Value = node };
            }
            foreach (var call in msg.Contents.OfType<FunctionCallContent>())
                yield return new ToolCallEvent { Turn = turn, Id = call.CallId, Name = call.Name, Arguments = ToJsonObject(call.Arguments) };
        }
        else
        {
            // User (and any unexpected role): a plain message, multipart if it carries images.
            yield return BuildMessage(msg, turn);
        }
    }

    // A trailing ```json fence, parsed, or null. Deliberately strict: only a fence that
    // actually closes the message and actually parses becomes an event, because a
    // consumer reading assistant_json must be able to trust it without re-validating.
    // Prose after the fence, an unterminated fence, or invalid JSON all yield nothing.
    private static JsonNode? ParseTrailingJsonFence(string text)
    {
        var trimmed = text.TrimEnd();
        if (!trimmed.EndsWith("```", StringComparison.Ordinal)) return null;

        var open = trimmed.LastIndexOf("```json", trimmed.Length - 3, StringComparison.OrdinalIgnoreCase);
        if (open < 0) return null;

        var bodyStart = open + "```json".Length;
        var body = trimmed[bodyStart..^3];
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Build the run-level <see cref="ResultEvent"/> trailer. Counts are derived
    /// from the emitted events; usage is passed in from the live response (it is
    /// not in history).
    /// </summary>
    public static ResultEvent ResultTrailer(IReadOnlyList<TranscriptEvent> events, string exitReason = "ok", UsageInfo? usage = null, string? harness = null, int deniedCount = 0, string? provider = null, int oracleTurns = 0, string? oracleVerdict = null, long? durationMs = null, long? providerMs = null, string? model = null, string? programSha256 = null, string? nbVersion = null, double? cost = null)
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
            DurationMs = durationMs,
            ProviderMs = providerMs,
            Provider = provider,
            Model = model,
            Cost = cost,
            ProgramSha256 = programSha256,
            NbVersion = nbVersion,
            Harness = harness,
            Denied = deniedCount > 0 ? deniedCount : null,
            OracleTurns = oracleTurns > 0 ? oracleTurns : null,
            OracleVerdict = oracleVerdict,
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
