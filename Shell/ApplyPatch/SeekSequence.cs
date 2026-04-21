using System.Text;

namespace nb.Shell.ApplyPatch;

/// <summary>
/// Locates a sequence of lines within a file using a cascade of equality checks:
/// exact → right-trimmed → fully-trimmed → Unicode-folded + fully-trimmed.
///
/// The cascade is load-bearing — real models often emit context with cosmetic whitespace
/// or curly-quote drift, and a naive exact match rejects those patches with "context not found".
/// </summary>
public static class SeekSequence
{
    /// <summary>Find the first occurrence of <paramref name="pattern"/> in <paramref name="hay"/>
    /// starting at index <paramref name="start"/>. Returns -1 if not found.</summary>
    public static int Find(IReadOnlyList<string> hay, IReadOnlyList<string> pattern, int start)
    {
        if (pattern.Count == 0) return start;
        if (start < 0) start = 0;
        if (start + pattern.Count > hay.Count) return -1;

        // Each mode is stricter first; short-circuit as soon as one matches.
        foreach (var mode in new[] { MatchMode.Exact, MatchMode.RTrim, MatchMode.FullTrim, MatchMode.FoldTrim })
        {
            for (int i = start; i + pattern.Count <= hay.Count; i++)
            {
                bool ok = true;
                for (int j = 0; j < pattern.Count; j++)
                {
                    if (!Equals(hay[i + j], pattern[j], mode)) { ok = false; break; }
                }
                if (ok) return i;
            }
        }
        return -1;
    }

    private enum MatchMode { Exact, RTrim, FullTrim, FoldTrim }

    private static bool Equals(string a, string b, MatchMode mode) => mode switch
    {
        MatchMode.Exact => a == b,
        MatchMode.RTrim => a.TrimEnd() == b.TrimEnd(),
        MatchMode.FullTrim => a.Trim() == b.Trim(),
        MatchMode.FoldTrim => Fold(a).Trim() == Fold(b).Trim(),
        _ => false,
    };

    /// <summary>Fold common Unicode drift back to ASCII. Mirrors codex's canonicalization.</summary>
    private static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '\u00A0' or '\u2007' or '\u202F' => ' ',           // non-breaking spaces
                '\u2013' or '\u2014' or '\u2212' => '-',            // en/em dash, minus
                '\u2018' or '\u2019' or '\u201A' or '\u2032' => '\'', // curly single quotes, prime
                '\u201C' or '\u201D' or '\u201E' or '\u2033' => '"', // curly double quotes
                '\u2026' => '.',                                    // ellipsis (first dot only — good enough)
                _ => c,
            });
        }
        return sb.ToString();
    }
}
