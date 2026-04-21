using nb.Shell.ApplyPatch;

namespace nb.Tests;

public class SeekSequenceTests
{
    [Fact]
    public void Find_ExactMatch()
    {
        var hay = new[] { "a", "b", "c", "d" };
        var needle = new[] { "b", "c" };
        Assert.Equal(1, SeekSequence.Find(hay, needle, 0));
    }

    [Fact]
    public void Find_RespectsStart_ForwardOnly()
    {
        var hay = new[] { "x", "y", "x", "y" };
        var needle = new[] { "x", "y" };
        Assert.Equal(0, SeekSequence.Find(hay, needle, 0));
        Assert.Equal(2, SeekSequence.Find(hay, needle, 1));
    }

    [Fact]
    public void Find_FallsBackToRTrim()
    {
        var hay = new[] { "hello   ", "world" };
        var needle = new[] { "hello", "world" };
        Assert.Equal(0, SeekSequence.Find(hay, needle, 0));
    }

    [Fact]
    public void Find_FallsBackToFullTrim()
    {
        var hay = new[] { "  indented", "    more" };
        var needle = new[] { "indented", "more" };
        Assert.Equal(0, SeekSequence.Find(hay, needle, 0));
    }

    [Fact]
    public void Find_FallsBackToUnicodeFold_CurlyQuotes()
    {
        var hay = new[] { "it\u2019s fine" };
        var needle = new[] { "it's fine" };
        Assert.Equal(0, SeekSequence.Find(hay, needle, 0));
    }

    [Fact]
    public void Find_FallsBackToUnicodeFold_EmDash()
    {
        var hay = new[] { "wait \u2014 really?" };
        var needle = new[] { "wait - really?" };
        Assert.Equal(0, SeekSequence.Find(hay, needle, 0));
    }

    [Fact]
    public void Find_ReturnsMinusOneWhenAbsent()
    {
        Assert.Equal(-1, SeekSequence.Find(new[] { "a", "b" }, new[] { "nope" }, 0));
    }

    [Fact]
    public void Find_EmptyPattern_ReturnsStart()
    {
        Assert.Equal(2, SeekSequence.Find(new[] { "a", "b", "c" }, Array.Empty<string>(), 2));
    }
}
