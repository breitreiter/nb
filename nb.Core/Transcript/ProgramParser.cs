using System.Text;

namespace nb.Transcript;

/// <summary>Raised when a source-syntax program line is malformed. Message names the line and the fix.</summary>
public sealed class ProgramParseException(string message) : Exception(message);

/// <summary>
/// Parses the line-oriented source syntax into transcript events (the bytecode).
/// See plans/conversation-program-evaluator.md ("The source syntax").
///
/// One rule: a logical line is <c>&lt;verb&gt; &lt;content&gt;</c> — the first
/// token is the verb, everything after the first space is content. Collisions
/// resolve by position (<c>system system design is hard</c> → verb <c>system</c>).
/// A trailing <c>\</c> continues content onto the next physical line (joined with
/// a newline). Blank lines and <c>#</c> comments (including a leading shebang) are
/// skipped. Structured events (<c>tool_call</c>/<c>tool_result</c>) are authored
/// as JSONL bytecode, not source.
///
/// This is the source→bytecode layer; it performs syntactic checks only (known
/// verb, required content, delta-token shape). Semantic resolution (do the names
/// exist, is the mode valid) belongs to the evaluator / --validate.
/// </summary>
public static class ProgramParser
{
    /// <summary>
    /// Parse source text into events. <paramref name="includeResolver"/> maps an
    /// <c>@path</c> token to file content; when null, an <c>@path</c> is left as
    /// literal text (a pure parse with no filesystem contact).
    /// </summary>
    public static IReadOnlyList<TranscriptEvent> Parse(string source, Func<string, string>? includeResolver = null)
    {
        var events = new List<TranscriptEvent>();
        int turn = 0;
        int lineNo = 0;

        foreach (var (logical, firstLineNo) in CoalesceLogicalLines(source))
        {
            lineNo = firstLineNo;
            var (verb, content) = SplitVerb(logical);

            switch (verb)
            {
                case "provider":
                    events.Add(new ProviderEvent { Name = RequireContent(verb, content, lineNo) });
                    break;
                case "model":
                    events.Add(new ModelEvent { Name = RequireContent(verb, content, lineNo) });
                    break;
                case "mcp":
                    events.Add(ParseSurface(new McpEvent(), content, lineNo));
                    break;
                case "tools":
                    events.Add(ParseSurface(new ToolsEvent(), content, lineNo));
                    break;
                case "approval":
                    events.Add(ParseApproval(content, lineNo));
                    break;
                case "system":
                    events.Add(new SystemEvent { Turn = turn++, Text = ResolveContent(content, includeResolver) });
                    break;
                case "user":
                    events.Add(new UserEvent { Turn = turn++, Text = ResolveContent(content, includeResolver) });
                    break;
                case "assistant":
                    events.Add(new AssistantTextEvent { Turn = turn++, Text = ResolveContent(content, includeResolver) });
                    break;
                case "run":
                    events.Add(new RunEvent { Turn = turn++, Prompt = content.Length == 0 ? null : ResolveContent(content, includeResolver) });
                    break;
                case "tool_call":
                case "tool_result":
                    throw new ProgramParseException($"line {lineNo}: '{verb}' has structured fields — author it as JSONL bytecode, not source syntax.");
                default:
                    throw new ProgramParseException(
                        $"line {lineNo}: unknown directive '{verb}'. Known: provider, model, mcp, tools, approval, system, user, assistant, run.");
            }
        }

        return events;
    }

    // Merge physical lines into logical lines: a line whose trimmed end is a lone
    // backslash continues onto the next, joined with '\n'. Skips blank lines and
    // '#' comments (covers a leading shebang). Yields (logicalLine, firstLineNo).
    private static IEnumerable<(string line, int lineNo)> CoalesceLogicalLines(string source)
    {
        var physical = source.Split('\n');
        var buf = new StringBuilder();
        int startLine = 0;
        bool building = false;

        for (int i = 0; i < physical.Length; i++)
        {
            var raw = physical[i].TrimEnd('\r');

            if (!building)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (raw.TrimStart().StartsWith("#")) continue; // comment / shebang
                startLine = i + 1;
            }

            var trimmed = raw.TrimEnd();
            if (trimmed.EndsWith("\\"))
            {
                buf.Append(trimmed[..^1].TrimEnd());
                buf.Append('\n');
                building = true;
            }
            else
            {
                buf.Append(raw);
                yield return (buf.ToString(), startLine);
                buf.Clear();
                building = false;
            }
        }

        if (building) // trailing continuation with no final line
            yield return (buf.ToString(), startLine);
    }

    private static (string verb, string content) SplitVerb(string logical)
    {
        var text = logical.TrimStart();
        int i = 0;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        var verb = text[..i];
        var content = i < text.Length ? text[(i + 1)..].Trim() : "";
        return (verb, content);
    }

    private static string RequireContent(string verb, string content, int lineNo) =>
        content.Length > 0
            ? content
            : throw new ProgramParseException($"line {lineNo}: '{verb}' requires a value.");

    // Parse "+name -name" delta tokens, or the lone "none" reset.
    private static SurfaceDirectiveEvent ParseSurface(SurfaceDirectiveEvent template, string content, int lineNo)
    {
        var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && tokens[0].Equals("none", StringComparison.OrdinalIgnoreCase))
            return template with { Reset = true };

        var add = new List<string>();
        var remove = new List<string>();
        foreach (var tok in tokens)
        {
            if (tok.Length < 2 || (tok[0] != '+' && tok[0] != '-'))
                throw new ProgramParseException($"line {lineNo}: '{template.Type}' tokens must be +name, -name, or 'none' — got '{tok}'.");
            (tok[0] == '+' ? add : remove).Add(tok[1..]);
        }
        return template with { Add = add, Remove = remove };
    }

    // Parse "approval <key> <value>": the first token is the key (bash|mcp|default|sandbox),
    // the rest is the value (a bash pattern may contain spaces). Key validity is a
    // semantic check (--validate / evaluator), not syntactic.
    private static ApprovalEvent ParseApproval(string content, int lineNo)
    {
        var trimmed = content.Trim();
        var sp = trimmed.IndexOf(' ');
        if (sp < 0)
            throw new ProgramParseException($"line {lineNo}: 'approval' needs '<key> <value>' (bash | mcp | default | sandbox) — got '{trimmed}'.");
        var value = trimmed[(sp + 1)..].Trim();
        if (value.Length == 0)
            throw new ProgramParseException($"line {lineNo}: 'approval {trimmed[..sp]}' needs a value.");
        return new ApprovalEvent { Key = trimmed[..sp].ToLowerInvariant(), Value = value };
    }

    // Whole-content @file include: content matching @<path> with no whitespace is
    // replaced by the file's contents; otherwise the text is literal.
    private static string ResolveContent(string content, Func<string, string>? includeResolver)
    {
        if (includeResolver is not null && content.StartsWith('@') && content.Length > 1
            && content.IndexOfAny(new[] { ' ', '\t', '\n' }) < 0)
        {
            return includeResolver(content[1..]);
        }
        return content;
    }
}
