namespace nb;

/// <summary>
/// The answer sheet an <c>oracle</c> directive attaches: a markdown body of headed
/// sections, each heading an entry id and its body the verbatim text the scripted user
/// gives when the oracle selects that id. Keyed by topic rather than by question because
/// models phrase the same question ten ways and a Q/A list matches badly.
/// See plans/oracle-resolver.md, "Answer sheet format".
/// </summary>
public sealed class AnswerSheet
{
    private readonly List<(string Id, string Body)> _entries;

    private AnswerSheet(List<(string, string)> entries) => _entries = entries;

    public IReadOnlyList<(string Id, string Body)> Entries => _entries;

    /// <summary>
    /// Parse the sheet. Any heading level (<c>#</c>, <c>##</c>, …) opens an entry whose
    /// id is the heading text; text before the first heading is ignored. Ids are compared
    /// case-insensitively, so a model that lowercases one still hits.
    /// </summary>
    public static AnswerSheet Parse(string markdown)
    {
        var entries = new List<(string, string)>();
        string? id = null;
        var body = new List<string>();

        void Close()
        {
            if (id is null) return;
            entries.Add((id, string.Join('\n', body).Trim()));
            body.Clear();
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith('#'))
            {
                Close();
                id = line.TrimStart('#').Trim();
                continue;
            }
            if (id is not null) body.Add(line);
        }
        Close();
        return new AnswerSheet(entries);
    }

    public bool Contains(string id) => Find(id) is not null;

    /// <summary>The entry's canonical id (as authored), or null if the sheet has none.</summary>
    public string? Find(string id) =>
        _entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)).Id;

    /// <summary>
    /// The user turn for a selection: the chosen bodies, verbatim, in sheet order,
    /// separated by a blank line. The oracle selects and never authors, so this is the
    /// only place the reply is composed.
    /// </summary>
    public string Compose(IEnumerable<string> ids)
    {
        var chosen = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
        return string.Join("\n\n", _entries.Where(e => chosen.Contains(e.Id)).Select(e => e.Body));
    }

    /// <summary>The sheet as shown to the oracle: ids and bodies, nothing else.</summary>
    public string Render() =>
        string.Join("\n\n", _entries.Select(e => $"### {e.Id}\n{e.Body}"));
}
