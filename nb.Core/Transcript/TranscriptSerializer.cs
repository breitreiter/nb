using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace nb.Transcript;

/// <summary>Raised when a transcript line is structurally invalid (bad JSON, missing required field). Message names the line.</summary>
public sealed class TranscriptFormatException(string message) : Exception(message);

/// <summary>
/// Reads and writes the transcript schema as JSONL (one JSON object per line).
/// This is the canonical form: what <c>--output jsonl</c> emits, what <c>--seed</c>
/// reads, what <c>/save</c> writes, and what a hook receives. Field ordering is
/// stable (type, turn, then type-specific) so output is diffable.
///
/// Structural parsing only: required-field checks and unknown-type skipping live
/// here. Cross-event validation (call/result pairing, turn monotonicity,
/// completed-round) belongs to the seed loader and is not enforced here.
/// </summary>
public static class TranscriptSerializer
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    // ---- Writing ----

    /// <summary>Serialize events to a JSONL string (one object per line, trailing newline).</summary>
    public static string Serialize(IEnumerable<TranscriptEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var ev in events)
            sb.Append(SerializeEvent(ev)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Write events to a <see cref="TextWriter"/> as they arrive — the streaming output path.</summary>
    public static void Write(TextWriter writer, IEnumerable<TranscriptEvent> events)
    {
        foreach (var ev in events)
            writer.Write(SerializeEvent(ev) + "\n");
    }

    /// <summary>Serialize a single event to one compact JSON line (no trailing newline).</summary>
    public static string SerializeEvent(TranscriptEvent ev)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
            WriteEvent(w, ev);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteEvent(Utf8JsonWriter w, TranscriptEvent ev)
    {
        w.WriteStartObject();
        w.WriteString("type", ev.Type);
        if (ev.Turn is int t) w.WriteNumber("turn", t); else w.WriteNull("turn");

        switch (ev)
        {
            case MessageEvent m:
                WriteMessageBody(w, m);
                break;
            case ThinkingEvent th:
                w.WriteString("text", th.Text);
                break;
            case ToolCallEvent tc:
                w.WriteString("id", tc.Id);
                w.WriteString("name", tc.Name);
                if (tc.Arguments is not null) { w.WritePropertyName("arguments"); tc.Arguments.WriteTo(w); }
                if (tc.Approved is not null) w.WriteString("approved", tc.Approved);
                break;
            case ToolResultEvent tr:
                w.WriteString("id", tr.Id);
                w.WriteString("output", tr.Output);
                if (tr.Result is not null) { w.WritePropertyName("result"); tr.Result.WriteTo(w); }
                break;
            case AssistantJsonEvent aj:
                w.WritePropertyName("value");
                if (aj.Value is null) w.WriteNullValue(); else aj.Value.WriteTo(w);
                break;
            case RunEvent run:
                if (run.Prompt is not null) w.WriteString("prompt", run.Prompt);
                break;
            case ProviderEvent p:
                w.WriteString("name", p.Name);
                break;
            case ModelEvent md:
                w.WriteString("name", md.Name);
                break;
            case SurfaceDirectiveEvent s:
                if (s.Reset) w.WriteBoolean("reset", true);
                WriteStringArray(w, "add", s.Add);
                WriteStringArray(w, "remove", s.Remove);
                break;
            case ApprovalEvent ap:
                w.WriteString("key", ap.Key);
                w.WriteString("value", ap.Value);
                break;
            case LoopEvent lp:
                w.WriteBoolean("enabled", lp.Enabled);
                if (lp.Enabled) w.WriteNumber("threshold", lp.Threshold);
                break;
            case BudgetEvent bg:
                w.WriteString("key", bg.Key);
                w.WriteNumber("value", bg.Value);
                break;
            case ResultEvent r:
                WriteResultBody(w, r);
                break;
        }

        w.WriteEndObject();
    }

    private static void WriteMessageBody(Utf8JsonWriter w, MessageEvent m)
    {
        if (m.Content is { } parts)
        {
            w.WritePropertyName("content");
            w.WriteStartArray();
            foreach (var p in parts) WriteContentPart(w, p);
            w.WriteEndArray();
        }
        else
        {
            w.WriteString("text", m.Text ?? "");
        }
    }

    private static void WriteContentPart(Utf8JsonWriter w, ContentPart p)
    {
        w.WriteStartObject();
        w.WriteString("kind", p.Kind);
        switch (p)
        {
            case TextPart t:
                w.WriteString("text", t.Text);
                break;
            case ImagePart im:
                if (im.MediaType is not null) w.WriteString("media_type", im.MediaType);
                if (im.Data is not null) w.WriteString("data", im.Data);
                if (im.Note is not null) w.WriteString("note", im.Note);
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return;
        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    private static void WriteResultBody(Utf8JsonWriter w, ResultEvent r)
    {
        w.WriteString("exit_reason", r.ExitReason);
        if (r.Usage is { } u)
        {
            w.WritePropertyName("usage");
            w.WriteStartObject();
            if (u.Input is { } i) w.WriteNumber("input", i);
            if (u.Output is { } o) w.WriteNumber("output", o);
            if (u.Total is { } tot) w.WriteNumber("total", tot);
            w.WriteEndObject();
        }
        if (r.Turns is { } turns) w.WriteNumber("turns", turns);
        if (r.ToolCalls is { } calls) w.WriteNumber("tool_calls", calls);
        if (r.DurationMs is { } d) w.WriteNumber("duration_ms", d);
    }

    // ---- Reading ----

    /// <summary>
    /// Parse a JSONL document into events. Blank lines are skipped; an unknown
    /// <c>type</c> is skipped with a warning appended to <paramref name="warnings"/>
    /// (forward-compat). Malformed JSON or a missing required field throws
    /// <see cref="TranscriptFormatException"/>.
    /// </summary>
    public static IReadOnlyList<TranscriptEvent> Parse(string jsonl, IList<string>? warnings = null)
    {
        var result = new List<TranscriptEvent>();
        var lines = jsonl.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var ev = ParseLine(lines[i], i + 1, warnings);
            if (ev is not null) result.Add(ev);
        }
        return result;
    }

    /// <summary>Parse one line. Returns null for a blank line or an unknown (skipped) event type.</summary>
    public static TranscriptEvent? ParseLine(string line, int lineNumber, IList<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new TranscriptFormatException($"line {lineNumber}: invalid JSON — {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new TranscriptFormatException($"line {lineNumber}: expected a JSON object, got {root.ValueKind}");
        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            throw new TranscriptFormatException($"line {lineNumber}: missing string field \"type\"");

        var type = typeEl.GetString()!;
        int? turn = ReadTurn(root);

        switch (type)
        {
            case "user":
                var (utext, ucontent) = ReadBody(root, lineNumber);
                return new UserEvent { Turn = turn, Text = utext, Content = ucontent };
            case "assistant_text":
                var (atext, acontent) = ReadBody(root, lineNumber);
                return new AssistantTextEvent { Turn = turn, Text = atext, Content = acontent };
            case "system":
                var (stext, scontent) = ReadBody(root, lineNumber);
                return new SystemEvent { Turn = turn, Text = stext, Content = scontent };
            case "thinking":
                return new ThinkingEvent { Turn = turn, Text = GetString(root, "text") ?? "" };
            case "tool_call":
                return new ToolCallEvent
                {
                    Turn = turn,
                    Id = RequireString(root, "id", lineNumber, type),
                    Name = RequireString(root, "name", lineNumber, type),
                    Arguments = ReadObject(root, "arguments"),
                    Approved = GetString(root, "approved"),
                };
            case "tool_result":
                return new ToolResultEvent
                {
                    Turn = turn,
                    Id = RequireString(root, "id", lineNumber, type),
                    Output = RequireString(root, "output", lineNumber, type),
                    Result = ReadObject(root, "result"),
                };
            case "assistant_json":
                return new AssistantJsonEvent { Turn = turn, Value = ReadNode(root, "value") };
            case "run":
                return new RunEvent { Turn = turn, Prompt = GetString(root, "prompt") };
            case "provider":
                return new ProviderEvent { Turn = turn, Name = RequireString(root, "name", lineNumber, type) };
            case "model":
                return new ModelEvent { Turn = turn, Name = RequireString(root, "name", lineNumber, type) };
            case "mcp":
                return new McpEvent { Turn = turn, Add = ReadStringArray(root, "add"), Remove = ReadStringArray(root, "remove"), Reset = GetBool(root, "reset") };
            case "tools":
                return new ToolsEvent { Turn = turn, Add = ReadStringArray(root, "add"), Remove = ReadStringArray(root, "remove"), Reset = GetBool(root, "reset") };
            case "approval":
                return new ApprovalEvent { Turn = turn, Key = RequireString(root, "key", lineNumber, type), Value = RequireString(root, "value", lineNumber, type) };
            case "loop":
                // Enabled unless explicitly false; threshold defaults to 0 (the evaluator floors it).
                var loopEnabled = !(root.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                return new LoopEvent { Turn = turn, Enabled = loopEnabled, Threshold = GetInt(root, "threshold") ?? 0 };
            case "budget":
                return new BudgetEvent { Turn = turn, Key = RequireString(root, "key", lineNumber, type), Value = RequireLong(root, "value", lineNumber, type) };
            case "result":
                return new ResultEvent
                {
                    Turn = turn,
                    ExitReason = GetString(root, "exit_reason") ?? "ok",
                    Usage = ReadUsage(root),
                    Turns = GetInt(root, "turns"),
                    ToolCalls = GetInt(root, "tool_calls"),
                    DurationMs = GetLong(root, "duration_ms"),
                };
            default:
                warnings?.Add($"line {lineNumber}: unknown event type \"{type}\" — skipped");
                return null;
        }
    }

    private static (string? text, IReadOnlyList<ContentPart>? content) ReadBody(JsonElement root, int lineNumber)
    {
        if (root.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<ContentPart>();
            foreach (var el in c.EnumerateArray())
                parts.Add(ReadContentPart(el, lineNumber));
            return (null, parts);
        }
        return (GetString(root, "text") ?? "", null);
    }

    private static ContentPart ReadContentPart(JsonElement el, int lineNumber)
    {
        var kind = el.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return kind switch
        {
            "text" => new TextPart { Text = GetString(el, "text") ?? "" },
            "image" => new ImagePart
            {
                MediaType = GetString(el, "media_type"),
                Data = GetString(el, "data"),
                Note = GetString(el, "note"),
            },
            _ => throw new TranscriptFormatException($"line {lineNumber}: unknown content part kind \"{kind}\""),
        };
    }

    private static UsageInfo? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
            return null;
        return new UsageInfo
        {
            Input = GetLong(u, "input"),
            Output = GetLong(u, "output"),
            Total = GetLong(u, "total"),
        };
    }

    private static int? ReadTurn(JsonElement root) =>
        root.TryGetProperty("turn", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var v)
            ? v : null;

    private static JsonObject? ReadObject(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(el.GetRawText()) as JsonObject
            : null;

    private static JsonNode? ReadNode(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? JsonNode.Parse(el.GetRawText()) : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.True;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;

    private static long? GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : null;

    private static string RequireString(JsonElement root, string name, int lineNumber, string type)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString()!;
        throw new TranscriptFormatException($"line {lineNumber}: {type} event is missing required string field \"{name}\"");
    }

    private static long RequireLong(JsonElement root, string name, int lineNumber, string type)
    {
        if (root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v))
            return v;
        throw new TranscriptFormatException($"line {lineNumber}: {type} event is missing required numeric field \"{name}\"");
    }
}
