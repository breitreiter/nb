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
/// The judge is seated AS the user, not as a third party asked whether the message
/// "asks for information" that an entry "answers". That framing missed every proposal
/// that stops for confirmation (an entry with a different value does not "answer" a
/// request to confirm), which is the case the sheet is most useful for. Asked instead
/// "which entries are your reply?", the correction is the obvious move. Measured in
/// evals/oracle-bench: 26/80 → 80/80 on qwen3-coder-next
/// (bugs/Oracle_Misses_A_Proposal_Awaiting_Confirmation.md). Change the wording only
/// with the bench in hand.
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
    /// to it and not to the sheet. The ids are listed by name: without that a small
    /// model answered with ordinals ("1", "1,2"), which parse as nothing and score DONE.
    /// </summary>
    public static List<ChatMessage> BuildPrompt(AnswerSheet sheet, string lastAssistantMessage) => new()
    {
        new(ChatRole.User,
            OracleProtocol.Sentinel + "\n" +
            "You are standing in for the user of an AI assistant. The assistant has just ended its turn with the message at the bottom. " +
            "The answer sheet is what you, the user, know and would say. Decide whether the assistant is waiting on you before it can go on, " +
            "and if so, which sheet entries are your reply.\n\n" +
            "The assistant is waiting on you when it asks you a question, asks you to confirm or approve something before it proceeds, " +
            "or proposes a value or a plan and stops for your say-so. A proposal that stops for confirmation is waiting on you exactly as a question is; " +
            "if an entry gives a different value from the one proposed, that entry is your reply — it corrects the proposal.\n\n" +
            "It is NOT waiting on you when it reports finished work, offers optional follow-ups you are free to ignore, " +
            "or raises a question it then answers itself.\n\n" +
            "Each sheet entry's id is its heading, exactly as written. The ids on this sheet are: " + IdList(sheet) + ".\n\n" +
            "Two examples, on a different sheet whose ids were `database-engine` and `region`:\n" +
            "- The assistant said \"I'll go with SQLite for now — let me know if you'd prefer something else before I set up the schema.\" and the `database-engine` entry said \"Postgres 16; we don't use SQLite anywhere.\" The reply is `database-engine`: it is waiting for your say-so and the entry corrects it.\n" +
            "- The assistant said \"Schema created and migrations pass. I can also add seed data if useful.\" The reply is DONE: finished work and an optional offer.\n\n" +
            "Reply with exactly one line and nothing else:\n" +
            "- the ids of the entries you would reply with, comma-separated, if the assistant is waiting on you and one or more entries are your reply - only the entries that speak to what it is waiting on, not every entry on the sheet;\n" +
            $"- {OracleProtocol.Miss} if it is waiting on you but nothing on the sheet is what you would say;\n" +
            $"- {OracleProtocol.Done} if it is not waiting on you.\n" +
            $"Never invent an id. If you cannot tell whether it is waiting on you, answer {OracleProtocol.Done}.\n\n" +
            "## Answer sheet\n\n" + sheet.Render() + "\n\n" +
            "## The assistant's last message\n" + OracleProtocol.SubjectMarker + "\n" + lastAssistantMessage)
    };

    private static string IdList(AnswerSheet sheet) => string.Join(", ", sheet.Entries.Select(e => "`" + e.Id + "`"));

    /// <summary>
    /// Options for the call: no tools, and an output cap wide enough for a thinking model.
    /// The verdict is one line, but on a reasoning model the reasoning is spent INSIDE the
    /// output cap: measured on GLM via llama-server, a 200-token cap returned an empty
    /// verdict every time (finish_reason=length, all of it reasoning), while 2000 tokens
    /// produced the right id after ~1500 tokens of thought. Turning thinking off instead
    /// gave a wrong verdict on a plain hit. So: room to think, and a truncation is
    /// reported as such rather than passed off as DONE.
    ///
    /// The temperature is the judge entry's configured <c>Temperature</c>, as the main run
    /// uses its own entry's — not a forced 0. A judge should be repeatable (with nothing set
    /// the call ran at the server's default, and a cell that measured 5/5 one afternoon
    /// measured 1/5 the next), so set 0 on the entry where the model allows it; the Claude 5
    /// family rejects the parameter outright ("temperature is deprecated for this model"),
    /// and an entry for one of those has to leave it unset.
    /// </summary>
    public static ChatOptions Options(float? temperature = null) => new() { MaxOutputTokens = 4096, Temperature = temperature };

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
