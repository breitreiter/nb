using System.Text;
using System.Text.Json.Nodes;

namespace nb.Transcript;

/// <summary>
/// Renders transcript events as porcelain: a plain-text, no-JSON-parser view of
/// a run for sed/grep consumers (plans/headless-machine-output.md, phase 1).
///
/// Mirrors the jsonl emit — post-hoc, over the same events — but each tool call
/// and result is one stable line (<c>TOOL &lt;name&gt; &lt;input&gt;</c> /
/// <c>RESULT &lt;output&gt;</c>) and assistant prose is written verbatim, so a
/// final <c>```json</c> fence survives byte-for-byte and
/// <c>sed -n '/```json/,/```/p'</c> just works. TOOL/RESULT payloads are escaped
/// to a single line to keep the "one record per line" grammar; prose is not.
///
/// Only model output and actions reach stdout: system and user events are input
/// (echoed as chrome to stderr during the run), and the run-level trailer goes
/// to stderr via <see cref="Trailer"/>.
/// </summary>
public static class TranscriptPorcelainWriter
{
    // The argument rendered inline after the tool name. First match wins;
    // anything without one of these falls back to compact JSON of all args.
    private static readonly string[] PrimaryArgKeys = { "command", "path", "pattern", "url", "query", "input" };

    public static string Write(IReadOnlyList<TranscriptEvent> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            switch (e)
            {
                case AssistantTextEvent a when !string.IsNullOrEmpty(a.Text):
                    sb.Append(a.Text);
                    if (!a.Text.EndsWith('\n')) sb.Append('\n');
                    break;
                case ToolCallEvent c:
                    sb.Append("TOOL ").Append(c.Name).Append(' ').Append(Escape(PrimaryArg(c.Arguments))).Append('\n');
                    break;
                case ToolResultEvent r:
                    sb.Append("RESULT ").Append(Escape(r.Output)).Append('\n');
                    break;
                // system / user / result-trailer: not on stdout
            }
        }
        return sb.ToString();
    }

    /// <summary>One-line run summary for stderr (telemetry, never mixed into stdout).</summary>
    public static string Trailer(ResultEvent r)
    {
        var sb = new StringBuilder($"nb: exit_reason={r.ExitReason}");
        if (r.Turns is { } t) sb.Append($" turns={t}");
        if (r.ToolCalls is { } tc) sb.Append($" tool_calls={tc}");
        if (r.Usage is { } u)
            sb.Append($" input={u.Input} output={u.Output} total={u.Total}");
        return sb.ToString();
    }

    private static string PrimaryArg(JsonObject? args)
    {
        if (args is null || args.Count == 0) return "";
        foreach (var key in PrimaryArgKeys)
            if (args.TryGetPropertyValue(key, out var v) && v is not null)
                return v.ToString();
        return args.ToJsonString();
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
}
