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
    private readonly int _limit;

    public ToolErrorTracker(int limit = DefaultLimit)
    {
        _limit = limit;
    }

    public int Limit => _limit;

    public void Reset() => _errorCounts.Clear();

    public void RecordResult(string toolName, bool isError)
    {
        if (isError)
        {
            _errorCounts[toolName] = _errorCounts.GetValueOrDefault(toolName) + 1;
        }
        else
        {
            _errorCounts.Remove(toolName);
        }
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
