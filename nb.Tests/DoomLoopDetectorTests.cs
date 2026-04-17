namespace nb.Tests;

public class DoomLoopDetectorTests
{
    [Fact]
    public void Empty_NoLoop()
    {
        var d = new DoomLoopDetector();
        Assert.Null(d.DetectLoop());
    }

    [Fact]
    public void BelowThreshold_NoLoop()
    {
        var d = new DoomLoopDetector();
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        Assert.Null(d.DetectLoop());
    }

    [Fact]
    public void ThreeIdenticalCalls_DetectsLoop()
    {
        var d = new DoomLoopDetector();
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        Assert.Equal(3, d.DetectLoop());
    }

    [Fact]
    public void DifferentArgs_NoLoop()
    {
        var d = new DoomLoopDetector();
        d.Record("read_file", "{\"path\":\"a.cs\"}");
        d.Record("read_file", "{\"path\":\"b.cs\"}");
        d.Record("read_file", "{\"path\":\"c.cs\"}");
        Assert.Null(d.DetectLoop());
    }

    [Fact]
    public void DifferentTool_ResetsPattern()
    {
        var d = new DoomLoopDetector();
        d.Record("bash", "{}");
        d.Record("bash", "{}");
        d.Record("read_file", "{}");
        Assert.Null(d.DetectLoop());
    }

    [Fact]
    public void RepeatingTriple_DetectsLoop()
    {
        var d = new DoomLoopDetector();
        // [read, grep, edit] x 3
        for (int i = 0; i < 3; i++)
        {
            d.Record("read_file", "{\"p\":\"x\"}");
            d.Record("grep", "{\"q\":\"y\"}");
            d.Record("edit_file", "{\"p\":\"x\"}");
        }
        Assert.Equal(3, d.DetectLoop());
    }

    [Fact]
    public void PatternPrefixedByNoise_StillDetects()
    {
        var d = new DoomLoopDetector();
        d.Record("read_file", "{\"p\":\"setup\"}");
        d.Record("bash", "{\"cmd\":\"ls\"}");
        // Then 3x [dotnet test]
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        d.Record("bash", "{\"cmd\":\"dotnet test\"}");
        Assert.Equal(3, d.DetectLoop());
    }

    [Fact]
    public void Reset_ClearsState()
    {
        var d = new DoomLoopDetector();
        d.Record("bash", "{}");
        d.Record("bash", "{}");
        d.Record("bash", "{}");
        Assert.NotNull(d.DetectLoop());
        d.Reset();
        Assert.Null(d.DetectLoop());
    }

    [Fact]
    public void CustomThreshold_Respected()
    {
        var d = new DoomLoopDetector(threshold: 2);
        d.Record("bash", "{}");
        d.Record("bash", "{}");
        Assert.Equal(2, d.DetectLoop());
    }
}
