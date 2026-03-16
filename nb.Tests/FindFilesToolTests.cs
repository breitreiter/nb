using nb.Shell;

namespace nb.Tests;

public class FindFilesToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShellEnvironment _env;
    private readonly FindFilesTool _tool;

    public FindFilesToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-find-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _env = CreateTestEnvironment(_testDir);
        _tool = new FindFilesTool(_env);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void FindFiles_SimpleGlob_MatchesCsFiles()
    {
        CreateFiles("Program.cs", "Util.cs", "readme.md", "data.json");

        var result = _tool.FindFiles("*.cs");

        Assert.True(result.Success);
        Assert.Equal(2, result.TotalMatches);
        Assert.Contains("Program.cs", result.Files);
        Assert.Contains("Util.cs", result.Files);
    }

    [Fact]
    public void FindFiles_RecursiveGlob_FindsNestedFiles()
    {
        CreateFiles("src/App.cs", "src/lib/Helper.cs", "tests/AppTests.cs");

        var result = _tool.FindFiles("**/*.cs");

        Assert.True(result.Success);
        Assert.Equal(3, result.TotalMatches);
    }

    [Fact]
    public void FindFiles_SkipsGitDirectory()
    {
        CreateFiles("src/App.cs", ".git/config", ".git/HEAD", ".git/objects/abc");

        var result = _tool.FindFiles("**/*");

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Files, f => f.Contains(".git"));
    }

    [Fact]
    public void FindFiles_SkipsNodeModules()
    {
        CreateFiles("index.js", "node_modules/pkg/index.js");

        var result = _tool.FindFiles("**/*.js");

        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Equal("index.js", result.Files[0]);
    }

    [Fact]
    public void FindFiles_SkipsBinObj()
    {
        CreateFiles("src/App.cs", "bin/Debug/net8.0/app.dll", "obj/project.assets.json");

        var result = _tool.FindFiles("**/*");

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Files, f => f.StartsWith("bin"));
        Assert.DoesNotContain(result.Files, f => f.StartsWith("obj"));
    }

    [Fact]
    public void FindFiles_RespectsMaxResults()
    {
        for (int i = 0; i < 10; i++)
            CreateFiles($"file{i}.txt");

        var result = _tool.FindFiles("*.txt", maxResults: 3);

        Assert.True(result.Success);
        Assert.Equal(3, result.Files.Length);
        Assert.Equal(10, result.TotalMatches);
        Assert.Contains("[Showing 3 of 10 matches", result.Output);
    }

    [Fact]
    public void FindFiles_SubdirectoryPath_SearchesFromThere()
    {
        CreateFiles("root.cs", "src/deep/file.cs");

        var result = _tool.FindFiles("**/*.cs", "src");

        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Contains("deep", result.Files[0]);
    }

    [Fact]
    public void FindFiles_NonexistentDirectory_ReturnsError()
    {
        var result = _tool.FindFiles("*.cs", "/nonexistent/path");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindFiles_NoMatches_ReturnsEmptySuccess()
    {
        CreateFiles("readme.md");

        var result = _tool.FindFiles("*.cs");

        Assert.True(result.Success);
        Assert.Empty(result.Files);
        Assert.Equal(0, result.TotalMatches);
    }

    [Fact]
    public void FindFiles_ResultsSortedAlphabetically()
    {
        CreateFiles("z.cs", "a.cs", "m.cs");

        var result = _tool.FindFiles("*.cs");

        Assert.True(result.Success);
        Assert.Equal(new[] { "a.cs", "m.cs", "z.cs" }, result.Files);
    }

    // --- Helpers ---

    private void CreateFiles(params string[] relativePaths)
    {
        foreach (var path in relativePaths)
        {
            var fullPath = Path.Combine(_testDir, path);
            var dir = Path.GetDirectoryName(fullPath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, $"// {path}");
        }
    }

    private static ShellEnvironment CreateTestEnvironment(string cwd)
    {
        // ShellEnvironment.Detect() sets cwd to Directory.GetCurrentDirectory().
        // We need to set the cwd to our test directory.
        var env = ShellEnvironment.Detect();
        env.SetCwd(cwd);
        return env;
    }
}
