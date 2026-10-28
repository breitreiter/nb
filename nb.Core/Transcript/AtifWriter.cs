using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace nb.Transcript;

/// <summary>
/// A run as an ATIF trajectory (Harbor's Agent Trajectory Interchange Format), written by
/// <c>--trajectory</c>. A pure function of the journal and the trailer
/// (<c>plans/atif-and-acp.md</c> stage 3).
///
/// The mapping is the one proctor's <c>Run/NbAtif.cs</c> applied to nb's JSONL, so a reader
/// of that sees nothing move: one agent step per turn (its text, thinking, tool calls and
/// their results), each user or system message its own step, and what ATIF has no field for
/// under <c>extra</c> in nb's words. On top, what only the journal knows: a step's
/// timestamp, its model, its round's token counts and cost, and that it was one model call.
/// Estimated usage is flagged as <c>metrics.extra.estimated</c>, never passed off as measured.
/// </summary>
public static class AtifWriter
{
    /// <summary>
    /// The pinned version: what proctor reads and what Harbor 0.24's own adapters write
    /// (Harbor's validator also accepts 1.8, which adds only audio parts). Proctor does not
    /// check the version, so a bump is checked against its reader by hand, on both sides.
    /// </summary>
    public const string SchemaVersion = "ATIF-v1.7";

    private static readonly JsonSerializerOptions FileOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A trajectory as the file <c>--trajectory</c> writes: indented, escaped only where JSON requires it, as the JSONL is.</summary>
    public static string Serialize(JsonObject trajectory) => trajectory.ToJsonString(FileOptions) + "\n";

    public static JsonObject Write(IReadOnlyList<JournalEntry> journal, ResultEvent trailer)
    {
        var steps = new JsonArray();
        JsonObject? agentStep = null;
        int? agentTurn = null;

        JsonObject AgentStep(JournalEntry entry)
        {
            if (agentStep is null || agentTurn != entry.Event.Turn)
            {
                agentStep = new JsonObject { ["step_id"] = steps.Count + 1, ["timestamp"] = Timestamp(entry), ["source"] = "agent", ["message"] = "" };
                agentTurn = entry.Event.Turn;
                steps.Add(agentStep);
            }
            if (entry.Round is { } round && agentStep["metrics"] is null) Measure(agentStep, round);
            return agentStep;
        }

        JsonObject Add(JournalEntry entry, string source, string message)
        {
            var step = new JsonObject { ["step_id"] = steps.Count + 1, ["timestamp"] = Timestamp(entry), ["source"] = source, ["message"] = message };
            steps.Add(step);
            agentStep = null;
            return step;
        }

        foreach (var entry in journal)
        {
            var ev = Json(entry.Event);
            switch (entry.Event)
            {
                case SystemEvent:
                    Add(entry, "system", Text(ev));
                    break;
                case UserEvent:
                    var user = Add(entry, "user", Text(ev));
                    var userExtra = new JsonObject();
                    if (ev["source"] is { } source) userExtra["source"] = source.DeepClone();
                    if (ev["keys"] is { } keys) userExtra["keys"] = keys.DeepClone();
                    if (userExtra.Count > 0) user["extra"] = userExtra;
                    break;
                case AssistantTextEvent:
                    var step = AgentStep(entry);
                    var text = Text(ev);
                    step["message"] = step["message"]!.GetValue<string>() is { Length: > 0 } before ? before + "\n" + text : text;
                    break;
                case ThinkingEvent:
                    AgentStep(entry)["reasoning_content"] = Text(ev);
                    break;
                case AssistantJsonEvent:
                    Extra(AgentStep(entry))["assistant_json"] = ev["value"]?.DeepClone();
                    break;
                case ToolCallEvent:
                    var call = new JsonObject
                    {
                        ["tool_call_id"] = Str(ev, "id") ?? "",
                        ["function_name"] = Str(ev, "name") ?? "",
                        ["arguments"] = ev["arguments"]?.DeepClone() ?? new JsonObject(),
                    };
                    var callExtra = new JsonObject();
                    if (Str(ev, "approved") is { } approved) callExtra["approved"] = approved;
                    if (Str(ev, "approval_reason") is { } reason) callExtra["approval_reason"] = reason;
                    if (callExtra.Count > 0) call["extra"] = callExtra;
                    (AgentStep(entry)["tool_calls"] ??= new JsonArray()).AsArray().Add(call);
                    break;
                case ToolResultEvent:
                    var result = new JsonObject { ["source_call_id"] = Str(ev, "id") ?? "", ["content"] = Str(ev, "output") ?? "" };
                    if (ev["result"] is JsonObject structured) result["extra"] = new JsonObject { ["result"] = structured.DeepClone() };
                    var observation = AgentStep(entry)["observation"] ??= new JsonObject { ["results"] = new JsonArray() };
                    observation["results"]!.AsArray().Add(result);
                    break;
                default:
                    // The journal holds conversation only; directives are never journaled.
                    throw new InvalidOperationException($"nb event type '{entry.Event.Type}' has no ATIF mapping");
            }
        }

        var trailerJson = Json(trailer);
        var trajectory = new JsonObject
        {
            ["schema_version"] = SchemaVersion,
            ["agent"] = new JsonObject { ["name"] = "nb", ["version"] = trailer.NbVersion?.Split('+')[0] ?? "", ["model_name"] = trailer.Model },
            ["steps"] = steps,
        };
        if (trailer.Usage is { } usage)
        {
            var final = new JsonObject
            {
                ["total_prompt_tokens"] = usage.Input,
                ["total_completion_tokens"] = usage.Output,
                ["total_steps"] = steps.Count,
            };
            if (trailer.Cost is { } cost) final["total_cost_usd"] = cost;
            if (usage.Estimated) final["extra"] = new JsonObject { ["estimated"] = true };
            trajectory["final_metrics"] = final;
        }
        trajectory["extra"] = new JsonObject { ["result"] = trailerJson };
        return trajectory;
    }

    // One agent step is one assistant message, so one model call: its round's numbers are the step's.
    private static void Measure(JsonObject step, RoundMetrics round)
    {
        if (round.Model is { } model) step["model_name"] = model;
        var metrics = new JsonObject { ["prompt_tokens"] = round.Input, ["completion_tokens"] = round.Output };
        if (round.CostUsd is { } cost) metrics["cost_usd"] = cost;
        if (round.Estimated) metrics["extra"] = new JsonObject { ["estimated"] = true };
        step["metrics"] = metrics;
        step["llm_call_count"] = 1;
    }

    private static string Timestamp(JournalEntry entry) => entry.At.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static JsonObject Json(TranscriptEvent ev) => JsonNode.Parse(TranscriptSerializer.SerializeEvent(ev))!.AsObject();

    // A multipart message flattens to its text, an image to its note, as proctor's reader does.
    private static string Text(JsonObject ev)
    {
        if (Str(ev, "text") is { } s) return s;
        if (ev["content"] is JsonArray parts)
            return string.Join("\n", parts.OfType<JsonObject>().Select(p => Str(p, "text") ?? Str(p, "note") ?? ""));
        return "";
    }

    private static JsonObject Extra(JsonObject step) => (step["extra"] ??= new JsonObject()).AsObject();

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
