using System.Text.RegularExpressions;

namespace nb.Shell;

public class ApprovalPatterns
{
    private readonly List<string> _exactPatterns = new();
    private readonly List<Regex> _globPatterns = new();

    public ApprovalPatterns()
    {
    }

    public ApprovalPatterns(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            Add(pattern);
        }
    }

    public void Add(string pattern)
    {
        if (pattern.Contains('*'))
        {
            // Convert glob to regex. Singleline so `.` crosses a newline: without it
            // `approval bash *` silently means "every command that fits on one line",
            // and a heredoc is denied by a rule that reads as matching everything.
            // Anchoring is untouched — a pattern still has to match from the first
            // character — so this widens the wildcard's reach, not the rule's escape
            // surface. A trailing `*` already spanned `;` and `&&` on one line; a
            // newline is another separator, not a new class of hole.
            // bugs/Approval_Bash_Glob_Does_Not_Match_Newlines.md
            var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            _globPatterns.Add(new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.Singleline));
        }
        else
        {
            _exactPatterns.Add(pattern);
        }
    }

    public bool IsApproved(string command)
    {
        var trimmed = command.Trim();

        // Check exact matches
        if (_exactPatterns.Contains(trimmed))
            return true;

        // For multi-word commands, also check if the base command matches
        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstWord != null && _exactPatterns.Contains(firstWord))
            return true;

        // Check glob patterns
        foreach (var regex in _globPatterns)
        {
            if (regex.IsMatch(trimmed))
                return true;
        }

        return false;
    }

    public int Count => _exactPatterns.Count + _globPatterns.Count;

    public bool HasPatterns => Count > 0;
}
