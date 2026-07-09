using UglyPrompt;

namespace nb.Shell;

// Provides the '@'-mention file completion source for the interactive line
// editor. Per the UglyPrompt 0.4.0 adoption guidance, the tree walk (the
// expensive part) runs at most once: the index is built lazily on first '@'
// use and then filtered in memory per keystroke — never re-enumerated. Bare
// relative-path Names commit under the existing '@' via Tab-to-accept.
public static class FileMentionSource
{
    // Hint strip is a single line; keep the candidate list short.
    private const int MaxResults = 15;
    // Bound the walk so a giant tree (or a symlink cycle) can't stall typing
    // or eat memory. Past this the index is best-effort partial.
    private const int MaxIndexSize = 20_000;

    public static CompletionSource Create(string rootDirectory)
    {
        var index = new Lazy<IReadOnlyList<string>>(() => BuildIndex(rootDirectory));

        return new CompletionSource('@', TriggerAnchor.WordStart,
            body => index.Value
                .Where(p => p.StartsWith(body, StringComparison.OrdinalIgnoreCase))
                .Take(MaxResults)
                .Select(p => new CompletionHint(p, ""))
                .ToList());
    }

    private static IReadOnlyList<string> BuildIndex(string root)
    {
        var results = new List<string>();
        try
        {
            Walk(root, root, results);
        }
        catch
        {
            // Best-effort: an unreadable directory must never crash typing.
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static void Walk(string root, string dir, List<string> results)
    {
        if (results.Count >= MaxIndexSize) return;

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            if (results.Count >= MaxIndexSize) return;
            results.Add(Path.GetRelativePath(root, file));
        }

        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            if (results.Count >= MaxIndexSize) return;
            if (DefaultSkipDirectories.All.Contains(Path.GetFileName(sub))) continue;
            Walk(root, sub, results);
        }
    }
}
