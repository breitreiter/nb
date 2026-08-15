namespace nb;

/// <summary>
/// Tracks consecutive failures per tool within a single turn.
/// Counts increment on error and reset on success for the same tool.
/// When any tool hits the limit, the turn is aborted to prevent runaway loops
/// (e.g. the model calling "dotnet test" 50 times in a row after it fails).
/// </summary>
public class ToolErrorTracker
{
    public const int DefaultLimit = 3;

    private readonly Dictionary<string, int> _errorCounts = new();
    // How many of the current streak were approval denials rather than task failures.
    // Kept alongside rather than folded into the count because the distinction only
    // matters at the moment the limit trips — see <see cref="StreakWasAllDenials"/>.
    private readonly Dictionary<string, int> _denialCounts = new();
    private readonly int _limit;

    public ToolErrorTracker(int limit = DefaultLimit)
    {
        _limit = limit;
    }

    public int Limit => _limit;

    public void Reset()
    {
        _errorCounts.Clear();
        _denialCounts.Clear();
    }

    /// <param name="isDenial">
    /// The failure was an approval denial, not a task failure. Lets a turn aborted purely
    /// by authorization report <c>approval_denied</c> (exit 4) instead of
    /// <c>tool_error_limit</c> (exit 3) — "the model was not permitted to proceed" and
    /// "the model kept failing at its task" are different outcomes to a caller.
    /// </param>
    public void RecordResult(string toolName, bool isError, bool isDenial = false)
    {
        if (isError)
        {
            _errorCounts[toolName] = _errorCounts.GetValueOrDefault(toolName) + 1;
            if (isDenial)
                _denialCounts[toolName] = _denialCounts.GetValueOrDefault(toolName) + 1;
        }
        else
        {
            _errorCounts.Remove(toolName);
            _denialCounts.Remove(toolName);
        }
    }

    /// <summary>
    /// Whether every failure in this tool's current streak was a denial.
    ///
    /// Deliberately all-or-nothing. A streak mixing denials with genuine tool failures is
    /// a task that went wrong and also hit a wall, and reporting that as
    /// <c>approval_denied</c> would tell a caller to go fix its approval policy when the
    /// policy was not the problem. Unanimity is the only reading that cannot mislead.
    /// </summary>
    public bool StreakWasAllDenials(string toolName)
    {
        var errors = _errorCounts.GetValueOrDefault(toolName);
        return errors > 0 && _denialCounts.GetValueOrDefault(toolName) == errors;
    }

    public int ErrorCount(string toolName) => _errorCounts.GetValueOrDefault(toolName);

    public int RemainingAttempts(string toolName) =>
        Math.Max(0, _limit - ErrorCount(toolName));

    public bool LimitReached(out string? offendingTool)
    {
        foreach (var (name, count) in _errorCounts)
        {
            if (count >= _limit)
            {
                offendingTool = name;
                return true;
            }
        }
        offendingTool = null;
        return false;
    }
}
