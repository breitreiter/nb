using nb.Shell;
using nb.Shell.ApplyPatch;

namespace nb.Tests;

public class PatchApplierTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileReadTracker _tracker;

    public PatchApplierTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-patch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _tracker = new FileReadTracker();
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [Fact]
    public void Apply_AddFile_WritesContent()
    {
        var patch = """
            *** Begin Patch
            *** Add File: hello.txt
            +line one
            +line two
            *** End Patch
            """;

        ApplyAndAssert(patch, Array.Empty<string>());

        var created = Path.Combine(_testDir, "hello.txt");
        Assert.Equal("line one\nline two", File.ReadAllText(created));
    }

    [Fact]
    public void Apply_AddFile_FailsIfExists()
    {
        File.WriteAllText(Path.Combine(_testDir, "exists.txt"), "already here");

        var patch = """
            *** Begin Patch
            *** Add File: exists.txt
            +new
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        Assert.Throws<PatchApplyException>(() => PatchApplier.BuildPreview(ops, _testDir, _tracker));
    }

    [Fact]
    public void Apply_UpdateFile_ReplacesMatchedBlock()
    {
        var path = SeedFile("code.cs", "void Foo()\n{\n    return 1;\n}\n");

        var patch = """
            *** Begin Patch
            *** Update File: code.cs
             void Foo()
             {
            -    return 1;
            +    return 42;
             }
            *** End Patch
            """;

        ApplyAndAssert(patch, new[] { path });

        Assert.Equal("void Foo()\n{\n    return 42;\n}\n", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_UpdateFile_RequiresPriorRead()
    {
        var path = SeedFile("a.txt", "a\nb\n");
        // intentionally do NOT RecordRead

        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            -a
            +A
             b
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var ex = Assert.Throws<PatchApplyException>(() => PatchApplier.BuildPreview(ops, _testDir, _tracker));
        Assert.Contains("must be read", ex.Message);
    }

    [Fact]
    public void Apply_UpdateFile_DetectsExternalModification()
    {
        var path = SeedFile("a.txt", "original\n");
        _tracker.RecordRead(path);

        // External modification between read and patch.
        File.WriteAllText(path, "mutated\n");

        var patch = """
            *** Begin Patch
            *** Update File: a.txt
            -original
            +changed
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var ex = Assert.Throws<PatchApplyException>(() => PatchApplier.BuildPreview(ops, _testDir, _tracker));
        Assert.Contains("modified since", ex.Message);
    }

    [Fact]
    public void Apply_DeleteFile_RemovesIt()
    {
        var path = SeedFile("gone.txt", "bye");

        var patch = """
            *** Begin Patch
            *** Delete File: gone.txt
            *** End Patch
            """;

        ApplyAndAssert(patch, Array.Empty<string>());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Apply_UpdateWithMove_WritesToNewLocationAndRemovesOld()
    {
        var oldPath = SeedFile("src/old.cs", "class Old {}\n");

        var patch = """
            *** Begin Patch
            *** Update File: src/old.cs
            *** Move to: src/new.cs
            -class Old {}
            +class New {}
            *** End Patch
            """;

        ApplyAndAssert(patch, new[] { oldPath });

        Assert.False(File.Exists(oldPath));
        var newPath = Path.Combine(_testDir, "src/new.cs");
        Assert.True(File.Exists(newPath));
        Assert.Equal("class New {}\n", File.ReadAllText(newPath));
    }

    [Fact]
    public void Apply_MultipleChunks_InOneFile_ForwardCursor()
    {
        var path = SeedFile("multi.txt", "a\nb\nc\na\nb\nc\n");

        // Without the forward cursor, the second chunk would target the first occurrence again.
        var patch = """
            *** Begin Patch
            *** Update File: multi.txt
            -a
            +A1
             b
             c
            -a
            +A2
             b
             c
            *** End Patch
            """;

        ApplyAndAssert(patch, new[] { path });

        Assert.Equal("A1\nb\nc\nA2\nb\nc\n", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_ContextHeader_DisambiguatesBetweenSimilarBlocks()
    {
        var path = SeedFile("ns.cs", """
            namespace First {
                void method() { return 1; }
            }
            namespace Second {
                void method() { return 1; }
            }

            """);

        var patch = """
            *** Begin Patch
            *** Update File: ns.cs
            @@ namespace Second {
            -    void method() { return 1; }
            +    void method() { return 2; }
            *** End Patch
            """;

        ApplyAndAssert(patch, new[] { path });

        var result = File.ReadAllText(path);
        Assert.Contains("namespace First {\n    void method() { return 1; }", result);
        Assert.Contains("namespace Second {\n    void method() { return 2; }", result);
    }

    [Fact]
    public void Apply_PreservesCrlfLineEndings()
    {
        var path = Path.Combine(_testDir, "win.txt");
        File.WriteAllText(path, "first\r\nsecond\r\n");
        _tracker.RecordRead(path);

        var patch = """
            *** Begin Patch
            *** Update File: win.txt
            -first
            +FIRST
             second
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var preview = PatchApplier.BuildPreview(ops, _testDir, _tracker);
        PatchApplier.Apply(preview, ops, _testDir, _tracker);

        Assert.Equal("FIRST\r\nsecond\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void Apply_UnicodeFold_LetsPatchUseAsciiApostrophe()
    {
        var path = SeedFile("text.txt", "it\u2019s working\n");

        var patch = """
            *** Begin Patch
            *** Update File: text.txt
            -it's working
            +it works
            *** End Patch
            """;

        ApplyAndAssert(patch, new[] { path });
        Assert.Equal("it works\n", File.ReadAllText(path));
    }

    // --- helpers ---

    private string SeedFile(string relPath, string content)
    {
        var full = Path.Combine(_testDir, relPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return full;
    }

    private void ApplyAndAssert(string patch, string[] readFirst)
    {
        foreach (var p in readFirst) _tracker.RecordRead(p);
        var ops = PatchParser.Parse(patch);
        var preview = PatchApplier.BuildPreview(ops, _testDir, _tracker);
        PatchApplier.Apply(preview, ops, _testDir, _tracker);
    }
}
