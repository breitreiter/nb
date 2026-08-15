namespace nb.Tests;

public class ToolErrorTrackerTests
{
    [Fact]
    public void Empty_NoLimit()
    {
        var t = new ToolErrorTracker();
        Assert.False(t.LimitReached(out _));
    }

    [Fact]
    public void ThreeConsecutiveErrors_HitsLimit()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);
        Assert.False(t.LimitReached(out _));
        t.RecordResult("bash", isError: true);
        Assert.True(t.LimitReached(out var tool));
        Assert.Equal("bash", tool);
    }

    [Fact]
    public void SuccessResetsCounterForSameTool()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: false);
        Assert.Equal(0, t.ErrorCount("bash"));
        Assert.False(t.LimitReached(out _));
    }

    [Fact]
    public void SuccessOfOtherTool_DoesNotResetCounter()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);
        t.RecordResult("read_file", isError: false);
        Assert.Equal(2, t.ErrorCount("bash"));
    }

    [Fact]
    public void RemainingAttempts_DecrementsWithErrors()
    {
        var t = new ToolErrorTracker(limit: 3);
        Assert.Equal(3, t.RemainingAttempts("bash"));
        t.RecordResult("bash", isError: true);
        Assert.Equal(2, t.RemainingAttempts("bash"));
        t.RecordResult("bash", isError: true);
        Assert.Equal(1, t.RemainingAttempts("bash"));
        t.RecordResult("bash", isError: true);
        Assert.Equal(0, t.RemainingAttempts("bash"));
    }

    [Fact]
    public void Reset_ClearsAllCounts()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);
        t.RecordResult("read_file", isError: true);
        t.Reset();
        Assert.Equal(0, t.ErrorCount("bash"));
        Assert.Equal(0, t.ErrorCount("read_file"));
        Assert.False(t.LimitReached(out _));
    }

    [Fact]
    public void PerToolIsolation()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("read_file", isError: true);
        t.RecordResult("bash", isError: true);
        t.RecordResult("read_file", isError: true);
        Assert.Equal(2, t.ErrorCount("bash"));
        Assert.Equal(2, t.ErrorCount("read_file"));
    }

    // ---- Denial streaks: what separates exit 4 from exit 3 -------------------------
    // A turn aborted purely by the approval policy is a different outcome from one the
    // model kept fumbling, and it is this flag that carries the difference.

    [Fact]
    public void AllDenials_MarksTheStreak()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true, isDenial: true);
        t.RecordResult("bash", isError: true, isDenial: true);
        t.RecordResult("bash", isError: true, isDenial: true);

        Assert.True(t.LimitReached(out var tool));
        Assert.True(t.StreakWasAllDenials(tool!));
    }

    [Fact]
    public void PlainFailures_AreNotADenialStreak()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);
        t.RecordResult("bash", isError: true);

        Assert.False(t.StreakWasAllDenials("bash"));
    }

    /// <summary>
    /// One genuine failure in the streak disqualifies it. A task that went wrong *and* hit
    /// a wall is not an authorization problem, and reporting it as one would send a caller
    /// to edit an approval policy that was never the blocker.
    /// </summary>
    [Fact]
    public void OneRealFailureAmongDenials_DisqualifiesTheStreak()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true, isDenial: true);
        t.RecordResult("bash", isError: true);                    // the odd one out
        t.RecordResult("bash", isError: true, isDenial: true);

        Assert.True(t.LimitReached(out _));
        Assert.False(t.StreakWasAllDenials("bash"));
    }

    [Fact]
    public void SuccessClearsTheDenialStreakToo()
    {
        var t = new ToolErrorTracker(limit: 3);
        t.RecordResult("bash", isError: true, isDenial: true);
        t.RecordResult("bash", isError: false);
        t.RecordResult("bash", isError: true);

        // The surviving streak is one plain failure, not a denial carried over.
        Assert.False(t.StreakWasAllDenials("bash"));
    }

    [Fact]
    public void NoErrors_IsNotVacuouslyADenialStreak()
    {
        var t = new ToolErrorTracker(limit: 3);
        Assert.False(t.StreakWasAllDenials("bash"));
    }
}
