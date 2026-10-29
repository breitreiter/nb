using System.Text.Encodings.Web;
using System.Text.Json;
using nb.Transcript;

namespace nb;

/// <summary>
/// One oracle judgement made outside a run (<see cref="Nb.JudgeAsync"/>, <c>nb --judge</c>):
/// what the scripted user would do with the message it was shown. <see cref="Answer"/> is
/// the reply composed from the sheet on a hit, and null otherwise.
/// </summary>
public sealed record JudgeResult
{
    public required OracleVerdictKind Verdict { get; init; }
    public IReadOnlyList<string> Keys { get; init; } = [];
    public string? Answer { get; init; }

    /// <summary>The judge's reply as it came back, as <c>oracle_verdict</c> on a run's trailer.</summary>
    public string Raw { get; init; } = "";

    /// <summary>The side call's usage as the provider reported it; null when it reported none or no call was made.</summary>
    public UsageInfo? Usage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The one-line JSON <c>nb --judge</c> prints.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("verdict", Verdict.ToString().ToLowerInvariant());
            w.WriteStartArray("keys");
            foreach (var k in Keys) w.WriteStringValue(k);
            w.WriteEndArray();
            if (Answer is not null) w.WriteString("answer", Answer);
            w.WriteString("raw", Raw);
            if (Usage is { } u)
            {
                w.WritePropertyName("usage");
                w.WriteStartObject();
                if (u.Input is { } i) w.WriteNumber("input", i);
                if (u.Output is { } o) w.WriteNumber("output", o);
                if (u.Total is { } t) w.WriteNumber("total", t);
                w.WriteEndObject();
            }
            w.WriteStartArray("warnings");
            foreach (var warning in Warnings) w.WriteStringValue(warning);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
