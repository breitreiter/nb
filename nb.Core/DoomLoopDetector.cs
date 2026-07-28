namespace nb;

/// <summary>
/// Detects repetitive tool-call patterns within a single turn.
/// Catches two failure modes:
///   1. Consecutive identical calls: [A,A,A]
///   2. Repeating sequences at the tail: [A,B,C][A,B,C][A,B,C]
/// Threshold is the number of repetitions required to trigger.
/// </summary>
public class DoomLoopDetector
{
    public const int DefaultThreshold = 3;

    private readonly int _threshold;
    private readonly List<(string Name, string Args)> _signatures = new();

    public DoomLoopDetector(int threshold = DefaultThreshold)
    {
        _threshold = threshold;
    }

    public int Threshold => _threshold;

    public void Reset() => _signatures.Clear();

    public void Record(string toolName, string argsJson)
    {
        _signatures.Add((toolName, argsJson));
    }

    /// <summary>
    /// Returns the number of tail repetitions if a loop is detected, else null.
    /// Considers patterns of length 1..N/threshold — picks the first (shortest)
    /// that hits the threshold.
    /// </summary>
    public int? DetectLoop()
    {
        if (_signatures.Count < _threshold) return null;

        for (int patternLen = 1; patternLen <= _signatures.Count / _threshold; patternLen++)
        {
            int reps = CountTailRepetitions(patternLen);
            if (reps >= _threshold) return reps;
        }
        return null;
    }

    private int CountTailRepetitions(int patternLen)
    {
        if (patternLen == 0 || _signatures.Count < patternLen) return 0;

        int total = _signatures.Count;
        int patternStart = total - patternLen;
        int reps = 1;
        int pos = patternStart;

        while (pos >= patternLen)
        {
            pos -= patternLen;
            bool match = true;
            for (int i = 0; i < patternLen; i++)
            {
                if (_signatures[pos + i] != _signatures[patternStart + i])
                {
                    match = false;
                    break;
                }
            }
            if (match) reps++;
            else break;
        }
        return reps;
    }
}
