using nb.Shell;

namespace nb.Tests;

public class GrepToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShellEnvironment _env;
    private readonly GrepTool _tool;

    public GrepToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-grep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _env = CreateTestEnvironment(_testDir);
        _tool = new GrepTool(_env);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void Grep_SimpleLiteral_FindsMatches()
    {
        CreateFile("app.cs", "using System;\nclass App { }\nclass Helper { }");

        var result = _tool.Grep("class");

        Assert.True(result.Success);
        Assert.Equal(2, result.TotalMatches);
        Assert.All(result.Matches, m => Assert.Contains("class", m));
    }

    [Fact]
    public void Grep_IncludesFileAndLineNumber()
    {
        CreateFile("test.txt", "line one\nline two\nfind me\nline four");

        var result = _tool.Grep("find me");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Matches(@"test\.txt:3: find me", result.Matches[0]);
    }

    [Fact]
    public void Grep_RegexPattern_Works()
    {
        CreateFile("data.cs", "int count = 42;\nstring name = \"hello\";\ndouble pi = 3.14;");

        var result = _tool.Grep(@"\d+\.\d+");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Contains("3.14", result.Matches[0]);
    }

    [Fact]
    public void Grep_CaseInsensitive_FindsBothCases()
    {
        CreateFile("mixed.txt", "Hello World\nhello world\nHELLO WORLD");

        var result = _tool.Grep("hello", caseInsensitive: true);

        Assert.True(result.Success);
        Assert.Equal(3, result.TotalMatches);
    }

    [Fact]
    public void Grep_CaseSensitive_Default()
    {
        CreateFile("mixed.txt", "Hello World\nhello world\nHELLO WORLD");

        var result = _tool.Grep("hello");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void Grep_FilePatternFilter_OnlySearchesMatchingFiles()
    {
        CreateFile("app.cs", "class Foo { }");
        CreateFile("readme.md", "class documentation");
        CreateFile("app.js", "class Bar { }");

        var result = _tool.Grep("class", filePattern: "*.cs");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Contains("app.cs", result.Matches[0]);
    }

    [Fact]
    public void Grep_RespectsMaxResults()
    {
        var lines = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"match line {i}"));
        CreateFile("big.txt", lines);

        var result = _tool.Grep("match", maxResults: 5);

        Assert.True(result.Success);
        Assert.Equal(5, result.Matches.Length);
        Assert.Equal(20, result.TotalMatches);
        Assert.Contains("[Showing 5 of 20 matches", result.Output);
    }

    [Fact]
    public void Grep_SkipsBinaryFiles()
    {
        // Create a file with null bytes (binary)
        var binaryPath = Path.Combine(_testDir, "binary.dat");
        File.WriteAllBytes(binaryPath, new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x00, 0x57, 0x6F, 0x72, 0x6C, 0x64 });

        CreateFile("text.txt", "Hello World");

        var result = _tool.Grep("Hello");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Contains("text.txt", result.Matches[0]);
    }

    [Fact]
    public void Grep_SkipsGitDirectory()
    {
        CreateFile("src/app.cs", "findme");
        CreateFile(".git/config", "findme");

        var result = _tool.Grep("findme");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.DoesNotContain(result.Matches, m => m.Contains(".git"));
    }

    [Fact]
    public void Grep_SkipsNodeModules()
    {
        CreateFile("index.js", "TODO: fix this");
        CreateFile("node_modules/pkg/lib.js", "TODO: fix this");

        var result = _tool.Grep("TODO");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void Grep_SingleFile_SearchesJustThatFile()
    {
        CreateFile("a.txt", "target line\nother");
        CreateFile("b.txt", "target line\nother");

        var result = _tool.Grep("target", path: Path.Combine(_testDir, "a.txt"));

        Assert.True(result.Success);
        Assert.Single(result.Matches);
    }

    [Fact]
    public void Grep_InvalidRegex_ReturnsError()
    {
        CreateFile("test.txt", "content");

        var result = _tool.Grep("[invalid");

        Assert.False(result.Success);
        Assert.Contains("Invalid regex", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grep_NoMatches_ReturnsEmptySuccess()
    {
        CreateFile("test.txt", "nothing here");

        var result = _tool.Grep("nonexistent");

        Assert.True(result.Success);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Grep_NonexistentPath_ReturnsError()
    {
        var result = _tool.Grep("pattern", path: "/nonexistent/dir");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Grep_LongLines_AreTruncated()
    {
        var longLine = "match " + new string('x', 300);
        CreateFile("long.txt", longLine);

        var result = _tool.Grep("match");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        // Line should be truncated with "..."
        Assert.EndsWith("...", result.Matches[0]);
        // Should be shorter than the original
        Assert.True(result.Matches[0].Length < longLine.Length);
    }

    [Fact]
    public void Grep_SubdirectoryPath_SearchesFromThere()
    {
        CreateFile("root.txt", "findme");
        CreateFile("sub/deep.txt", "findme");

        var result = _tool.Grep("findme", path: "sub");

        Assert.True(result.Success);
        Assert.Single(result.Matches);
        Assert.Contains("deep.txt", result.Matches[0]);
    }

    // --- Helpers ---

    private void CreateFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_testDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    private static ShellEnvironment CreateTestEnvironment(string cwd)
    {
        var env = ShellEnvironment.Detect();
        env.SetCwd(cwd);
        return env;
    }
}
