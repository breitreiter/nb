using Microsoft.Extensions.AI;
using nb.Transcript;

namespace nb;

/// <summary>What the oracle decided about a finished turn.</summary>
public sealed record OracleVerdict(OracleVerdictKind Kind, IReadOnlyList<string> Keys, string Raw)
{
    public static OracleVerdict Done(string raw) => new(OracleVerdictKind.Done, Array.Empty<string>(), raw);
    public static OracleVerdict Miss(string raw) => new(OracleVerdictKind.Miss, Array.Empty<string>(), raw);
    public static OracleVerdict Hit(IReadOnlyList<string> keys, string raw) => new(OracleVerdictKind.Hit, keys, raw);
}

public enum OracleVerdictKind { Done, Miss, Hit }

/// <summary>
/// The oracle side call: one small, separate model call that decides whether a finished
/// run was clearly waiting on the user for something the answer sheet covers, and if so
/// which entries resolve it. Classification and selection collapse into one prompt,
/// scoped to the sheet — "is it asking about one of these?" is a far easier question
/// than "is it asking?". See plans/oracle-resolver.md, "One oracle call does two jobs".
///
/// The oracle selects and never authors: the verdict is ids, <c>DONE</c> or
/// <c>MISS</c>, and the reply is composed from the sheet by <see cref="AnswerSheet"/>.
/// </summary>
public static class OracleResolver
{
    /// <summary>
    /// Build the call. The whole prompt is ONE user message opening with
    /// <see cref="OracleProtocol.Sentinel"/> — that placement is the contract the Mock
    /// provider keys on, and the distinct prefix is what keeps this call's cache entry
    /// apart from the subject conversation's. The judged message comes LAST, behind
    /// <see cref="OracleProtocol.SubjectMarker"/>, so the Mock can scope its rider scan
    /// to it and not to the sheet.
    /// </summary>
    public static List<ChatMessage> BuildPrompt(AnswerSheet sheet, string lastAssistantMessage) => new()
    {
        new(ChatRole.User,
            OracleProtocol.Sentinel + "\n" +
            "You are judging the last message of an AI assistant on behalf of the user it is talking to. " +
            "Decide whether that message is CLEARLY waiting on the user for information, and whether the " +
            "answer sheet below covers what it asks.\n\n" +
            "Reply with exactly one line and nothing else:\n" +
            "- the ids of the sheet entries that answer what the assistant is asking, comma-separated, if it is clearly asking the user for something one or more entries cover;\n" +
            $"- {OracleProtocol.Miss} if it is clearly asking the user for something, but no entry covers it;\n" +
            $"- {OracleProtocol.Done} if it is not clearly waiting on the user (finished, reporting, offering optional follow-ups, or ambiguous).\n" +
            $"Be conservative: when in doubt, {OracleProtocol.Done}. Never invent an id.\n\n" +
            "## Answer sheet\n\n" + sheet.Render() + "\n\n" +
            "## The assistant's last message\n" + OracleProtocol.SubjectMarker + "\n" + lastAssistantMessage)
    };

    /// <summary>
    /// Options for the call: no tools, and an output cap wide enough for a thinking model.
    /// The verdict is one line, but on a reasoning model the reasoning is spent INSIDE the
    /// output cap: measured on GLM via llama-server, a 200-token cap returned an empty
    /// verdict every time (finish_reason=length, all of it reasoning), while 2000 tokens
    /// produced the right id after ~1500 tokens of thought. Turning thinking off instead
    /// gave a wrong verdict on a plain hit. So: room to think, and a truncation is
    /// reported as such rather than passed off as DONE.
    /// </summary>
    public static ChatOptions Options() => new() { MaxOutputTokens = 4096 };

    /// <summary>
    /// Read the verdict. Unknown ids are dropped; a reply with no usable id and no
    /// terminal token is DONE — the conservative rule applies to a malformed verdict
    /// too, and a warning names it so it is not a silent miss-classification.
    /// </summary>
    public static OracleVerdict ParseVerdict(ChatResponse response, AnswerSheet sheet, IList<string> warnings)
    {
        var raw = (response.Text ?? "").Trim();
        if (raw.Length == 0 && response.FinishReason == ChatFinishReason.Length)
        {
            warnings.Add($"oracle: the verdict was cut off by the output cap before any text arrived (a reasoning model spent it thinking) — treated as {OracleProtocol.Done}");
            return OracleVerdict.Done(raw);
        }
        return ParseVerdict(raw, sheet, warnings);
    }

    /// <summary>Read a verdict from its text alone.</summary>
    public static OracleVerdict ParseVerdict(string? response, AnswerSheet sheet, IList<string> warnings)
    {
        var raw = (response ?? "").Trim();
        // First non-empty line, with any code fence or backticks the model wrapped it in removed.
        var line = raw.Split('\n').Select(l => l.Trim().Trim('`').Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("```")) ?? "";
        line = line.TrimEnd('.', '!');

        if (line.Equals(OracleProtocol.Done, StringComparison.OrdinalIgnoreCase)) return OracleVerdict.Done(raw);
        if (line.Equals(OracleProtocol.Miss, StringComparison.OrdinalIgnoreCase)) return OracleVerdict.Miss(raw);

        var keys = new List<string>();
        var unknown = new List<string>();
        foreach (var token in line.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var id = sheet.Find(token.Trim('`', '"', '\'', '.'));
            if (id is null) unknown.Add(token);
            else if (!keys.Contains(id)) keys.Add(id);
        }

        if (unknown.Count > 0)
            warnings.Add($"oracle: verdict named ids not on the sheet ({string.Join(", ", unknown)}) — ignored");
        if (keys.Count == 0)
        {
            warnings.Add($"oracle: unreadable verdict '{Truncate(raw)}' — treated as {OracleProtocol.Done}");
            return OracleVerdict.Done(raw);
        }
        return OracleVerdict.Hit(keys, raw);
    }

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "…";
}
