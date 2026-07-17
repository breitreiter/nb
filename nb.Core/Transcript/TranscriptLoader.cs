using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace nb.Transcript;

/// <summary>
/// Reconstructs model-facing conversation history from a transcript — the load
/// half of S2 (seed input). See plans/transcript-schema.md.
///
/// This is the inverse of <see cref="TranscriptMapper"/> over the core events.
/// Enrichment (thinking, assistant_json, the result trailer, and the
/// approved/result fields) is ignored: output is a superset, seed-load reads the
/// subset it needs. The reconstructed history equals the original run's history
/// modulo that enrichment (and images, which collapse to their text note).
///
/// Validation enforces the schema's contract before reconstruction, with
/// model-fixable errors: tool_call/tool_result pairing per turn, turn
/// monotonicity, and the v1 completed-round requirement (every call answered).
/// </summary>
public static class TranscriptLoader
{
    /// <summary>Parse JSONL and reconstruct history in one step.</summary>
    public static List<ChatMessage> Load(string jsonl, IList<string>? warnings = null) =>
        ToHistory(TranscriptSerializer.Parse(jsonl, warnings));

    /// <summary>Validate and reconstruct history from already-parsed events.</summary>
    public static List<ChatMessage> ToHistory(IReadOnlyList<TranscriptEvent> events)
    {
        Validate(events);

        var messages = new List<ChatMessage>();
        foreach (var group in GroupByTurn(events))
        {
            foreach (var sys in group.OfType<SystemEvent>())
                messages.Add(new ChatMessage(ChatRole.System, ToContents(sys)));

            foreach (var usr in group.OfType<UserEvent>())
                messages.Add(new ChatMessage(ChatRole.User, ToContents(usr)));

            // One assistant message per turn: its text and all its tool calls.
            var texts = group.OfType<AssistantTextEvent>().ToList();
            var calls = group.OfType<ToolCallEvent>().ToList();
            if (texts.Count > 0 || calls.Count > 0)
            {
                var contents = new List<AIContent>();
                var text = string.Concat(texts.Select(t => t.Text ?? ""));
                if (!string.IsNullOrEmpty(text)) contents.Add(new TextContent(text));
                foreach (var c in calls)
                    contents.Add(new FunctionCallContent(c.Id, c.Name, ToArguments(c.Arguments)));
                messages.Add(new ChatMessage(ChatRole.Assistant, contents));
            }

            // One tool message per turn: all its results.
            var results = group.OfType<ToolResultEvent>().ToList();
            if (results.Count > 0)
                messages.Add(new ChatMessage(ChatRole.Tool,
                    results.Select(r => (AIContent)new FunctionResultContent(r.Id, r.Output)).ToList()));
        }

        return messages;
    }

    public static void Validate(IReadOnlyList<TranscriptEvent> events)
    {
        // Turn monotonicity over message-bearing events.
        int? lastTurn = null;
        foreach (var e in events.Where(IsCore))
        {
            if (e.Turn is int t)
            {
                if (lastTurn is int lt && t < lt)
                    throw new TranscriptFormatException($"turn {t} appears after turn {lt}: turns must be non-decreasing");
                lastTurn = t;
            }
        }

        // tool_call/tool_result pairing per turn: a result must follow its call
        // in the same turn, and every call must be answered (v1 completed-round).
        var callsByTurn = new Dictionary<int, List<string>>();
        var resultsByTurn = new Dictionary<int, List<string>>();
        var seenCalls = new HashSet<(int turn, string id)>();

        foreach (var e in events)
        {
            switch (e)
            {
                case ToolCallEvent tc:
                    AddTo(callsByTurn, tc.Turn ?? -1, tc.Id);
                    seenCalls.Add((tc.Turn ?? -1, tc.Id));
                    break;
                case ToolResultEvent tr:
                    int rt = tr.Turn ?? -1;
                    if (!seenCalls.Contains((rt, tr.Id)))
                        throw new TranscriptFormatException(
                            $"tool_result id \"{tr.Id}\" (turn {rt}) has no preceding tool_call with that id in the same turn");
                    AddTo(resultsByTurn, rt, tr.Id);
                    break;
            }
        }

        foreach (var (turn, ids) in callsByTurn)
        {
            var answered = resultsByTurn.TryGetValue(turn, out var r) ? r : new List<string>();
            foreach (var id in ids)
                if (!answered.Contains(id))
                    throw new TranscriptFormatException(
                        $"tool_call id \"{id}\" (turn {turn}) has no matching tool_result — a seed must end on a completed round");
        }
    }

    private static IEnumerable<List<TranscriptEvent>> GroupByTurn(IReadOnlyList<TranscriptEvent> events)
    {
        var byTurn = new Dictionary<int, List<TranscriptEvent>>();
        var order = new List<int>();
        foreach (var e in events.Where(IsCore))
        {
            int t = e.Turn ?? -1;
            if (!byTurn.TryGetValue(t, out var list)) { byTurn[t] = list = new(); order.Add(t); }
            list.Add(e);
        }
        return order.Select(t => byTurn[t]);
    }

    private static bool IsCore(TranscriptEvent e) =>
        e is UserEvent or SystemEvent or AssistantTextEvent or ToolCallEvent or ToolResultEvent;

    private static List<AIContent> ToContents(MessageEvent m)
    {
        if (m.Content is { } parts)
        {
            var list = new List<AIContent>();
            foreach (var p in parts)
            {
                switch (p)
                {
                    case TextPart t:
                        list.Add(new TextContent(t.Text));
                        break;
                    case ImagePart img:
                        // Images don't round-trip; the note text stands in for the model.
                        list.Add(new TextContent(img.Note ?? "[image]"));
                        break;
                }
            }
            return list;
        }
        return new List<AIContent> { new TextContent(m.Text ?? "") };
    }

    private static IDictionary<string, object?>? ToArguments(JsonObject? args)
    {
        if (args is null) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var kvp in args)
            dict[kvp.Key] = kvp.Value?.DeepClone(); // parentless node; re-serializes to the same JSON
        return dict;
    }

    private static void AddTo(Dictionary<int, List<string>> d, int key, string value)
    {
        if (!d.TryGetValue(key, out var list)) d[key] = list = new();
        list.Add(value);
    }
}
