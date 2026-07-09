using nb.Shell;
using UglyPrompt;

namespace nb.Tests;

public class FileMentionSourceTests : IDisposable
{
    private readonly string _testDir;

    public FileMentionSourceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-atfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void Create_UsesAtTriggerAtWordStart()
    {
        var source = FileMentionSource.Create(_testDir);

        Assert.Equal('@', source.Trigger);
        Assert.Equal(TriggerAnchor.WordStart, source.Anchor);
    }

    [Fact]
    public void Lookup_EmptyBody_ReturnsAllFilesAsBareRelativePaths()
    {
        CreateFiles("Program.cs", "readme.md");

        var names = Lookup(_testDir, "");

        Assert.Contains("Program.cs", names);
        Assert.Contains("readme.md", names);
        // Bare paths — no leading '@' (Tab-accept commits under the existing sigil).
        Assert.DoesNotContain(names, n => n.StartsWith('@'));
    }

    [Fact]
    public void Lookup_Prefix_FiltersByStartsWith()
    {
        CreateFiles("App.cs", "Banana.cs");

        var names = Lookup(_testDir, "App");

        Assert.Equal(new[] { "App.cs" }, names);
    }

    [Fact]
    public void Lookup_IsCaseInsensitive()
    {
        CreateFiles("App.cs");

        var names = Lookup(_testDir, "app");

        Assert.Contains("App.cs", names);
    }

    [Fact]
    public void Lookup_IncludesNestedFilesWithRelativePath()
    {
        CreateFiles("src/lib/Helper.cs");

        var names = Lookup(_testDir, "src");

        var match = Assert.Single(names);
        Assert.Contains("Helper.cs", match);
        Assert.Contains("lib", match);
    }

    [Fact]
    public void Lookup_SkipsIgnoredDirectories()
    {
        CreateFiles(
            "src/App.cs",
            "bin/app.dll",
            "obj/project.assets.json",
            ".git/config",
            "node_modules/pkg/index.js");

        var names = Lookup(_testDir, "");

        Assert.Contains(names, n => n.Contains("App.cs"));
        Assert.DoesNotContain(names, n => n.Contains("app.dll"));
        Assert.DoesNotContain(names, n => n.Contains("project.assets.json"));
        Assert.DoesNotContain(names, n => n.Contains("config"));
        Assert.DoesNotContain(names, n => n.Contains("index.js"));
    }

    [Fact]
    public void Lookup_CapsResultCount()
    {
        for (int i = 0; i < 25; i++)
            CreateFiles($"file{i:D2}.txt");

        var names = Lookup(_testDir, "");

        Assert.Equal(15, names.Count); // MaxResults
    }

    [Fact]
    public void Lookup_NoMatch_ReturnsEmpty()
    {
        CreateFiles("readme.md");

        var names = Lookup(_testDir, "zzz");

        Assert.Empty(names);
    }

    // --- Helpers ---

    private static IReadOnlyList<string> Lookup(string root, string body)
        => FileMentionSource.Create(root).Lookup(body).Select(h => h.Name).ToList();

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
}
