using nb.Shell;

namespace nb.Tests;

public class ListDirToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShellEnvironment _env;
    private readonly ListDirTool _tool;

    public ListDirToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-listdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);

        _env = CreateTestEnvironment(_testDir);
        _tool = new ListDirTool(_env);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void ListDir_ListsFilesAndDirectories()
    {
        CreateFiles("Program.cs", "readme.md", "src/App.cs");

        var result = _tool.ListDir();

        Assert.True(result.Success);
        Assert.Contains("[dir]  src", result.Output);
        Assert.Contains("[file] Program.cs", result.Output);
        Assert.Contains("[file] readme.md", result.Output);
    }

    [Fact]
    public void ListDir_SkipsBuildDirectories()
    {
        CreateFiles("src/App.cs", "bin/app.dll", "obj/assets.json", ".git/HEAD");

        var result = _tool.ListDir();

        Assert.True(result.Success);
        Assert.Equal("[dir]  src", result.Output);
    }

    [Fact]
    public void ListDir_SubdirectoryPath_ListsFromThere()
    {
        CreateFiles("root.cs", "src/deep.cs");

        var result = _tool.ListDir("src");

        Assert.True(result.Success);
        Assert.Equal("[file] deep.cs", result.Output);
    }

    [Fact]
    public void ListDir_EmptyDirectory_ReportsEmpty()
    {
        var result = _tool.ListDir();

        Assert.True(result.Success);
        Assert.Equal("(empty directory)", result.Output);
    }

    [Fact]
    public void ListDir_NonexistentDirectory_ReturnsError()
    {
        var result = _tool.ListDir("/nonexistent/path");

        Assert.False(result.Success);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
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
        var env = ShellEnvironment.Detect();
        env.SetCwd(cwd);
        return env;
    }
}
