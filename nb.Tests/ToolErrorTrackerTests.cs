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
}
