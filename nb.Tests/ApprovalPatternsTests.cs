using nb.Shell;

namespace nb.Tests;

public class ApprovalPatternsTests
{
    [Fact]
    public void IsApproved_EmptyPatterns_ReturnsFalse()
    {
        var patterns = new ApprovalPatterns();

        Assert.False(patterns.IsApproved("ls"));
        Assert.False(patterns.IsApproved("any command"));
    }

    [Fact]
    public void IsApproved_ExactMatch_ReturnsTrue()
    {
        var patterns = new ApprovalPatterns(new[] { "ls", "git status" });

        Assert.True(patterns.IsApproved("ls"));
        Assert.True(patterns.IsApproved("git status"));
    }

    [Fact]
    public void IsApproved_ExactMatchWithExtraArgs_ReturnsTrue()
    {
        // When pattern is "ls", "ls -la" should also be approved
        var patterns = new ApprovalPatterns(new[] { "ls" });

        Assert.True(patterns.IsApproved("ls"));
        Assert.True(patterns.IsApproved("ls -la"));
        Assert.True(patterns.IsApproved("ls /tmp"));
    }

    [Fact]
    public void IsApproved_NoMatch_ReturnsFalse()
    {
        var patterns = new ApprovalPatterns(new[] { "ls", "cat" });

        Assert.False(patterns.IsApproved("rm file.txt"));
        Assert.False(patterns.IsApproved("git push"));
    }

    [Fact]
    public void IsApproved_GlobPattern_MatchesWildcard()
    {
        var patterns = new ApprovalPatterns(new[] { "git *" });

        Assert.True(patterns.IsApproved("git status"));
        Assert.True(patterns.IsApproved("git diff"));
        Assert.True(patterns.IsApproved("git log --oneline"));
        Assert.False(patterns.IsApproved("ls -la"));
    }

    [Fact]
    public void IsApproved_GlobPatternWithPath_MatchesPath()
    {
        var patterns = new ApprovalPatterns(new[] { "cat *.txt" });

        Assert.True(patterns.IsApproved("cat file.txt"));
        Assert.True(patterns.IsApproved("cat readme.txt"));
        Assert.False(patterns.IsApproved("cat file.md"));
    }

    [Fact]
    public void IsApproved_TrimsWhitespace()
    {
        var patterns = new ApprovalPatterns(new[] { "ls" });

        Assert.True(patterns.IsApproved("  ls  "));
        Assert.True(patterns.IsApproved("\tls\n"));
    }

    [Fact]
    public void Add_CanAddPatternsAfterConstruction()
    {
        var patterns = new ApprovalPatterns();
        Assert.False(patterns.IsApproved("ls"));

        patterns.Add("ls");
        Assert.True(patterns.IsApproved("ls"));
    }

    [Fact]
    public void Count_ReturnsCorrectCount()
    {
        var patterns = new ApprovalPatterns();
        Assert.Equal(0, patterns.Count);

        patterns.Add("ls");
        Assert.Equal(1, patterns.Count);

        patterns.Add("git *");
        Assert.Equal(2, patterns.Count);
    }

    [Fact]
    public void HasPatterns_ReturnsCorrectValue()
    {
        var patterns = new ApprovalPatterns();
        Assert.False(patterns.HasPatterns);

        patterns.Add("ls");
        Assert.True(patterns.HasPatterns);
    }
}
