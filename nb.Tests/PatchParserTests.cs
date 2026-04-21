using nb.Shell.ApplyPatch;

namespace nb.Tests;

public class PatchParserTests
{
    [Fact]
    public void Parse_AddFile_CapturesContent()
    {
        var patch = """
            *** Begin Patch
            *** Add File: hello.txt
            +line one
            +line two
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var add = Assert.IsType<AddFile>(Assert.Single(ops));
        Assert.Equal("hello.txt", add.Path);
        Assert.Equal("line one\nline two", add.Content);
    }

    [Fact]
    public void Parse_DeleteFile_NoBody()
    {
        var patch = """
            *** Begin Patch
            *** Delete File: gone.txt
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var del = Assert.IsType<DeleteFile>(Assert.Single(ops));
        Assert.Equal("gone.txt", del.Path);
    }

    [Fact]
    public void Parse_UpdateFile_WithContextHeaderAndChanges()
    {
        var patch = """
            *** Begin Patch
            *** Update File: a.cs
            @@ class Foo
             void Bar()
            -    return 1;
            +    return 2;
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var upd = Assert.IsType<UpdateFile>(Assert.Single(ops));
        Assert.Equal("a.cs", upd.Path);
        Assert.Null(upd.MoveTo);
        var chunk = Assert.Single(upd.Chunks);
        Assert.Equal(new[] { "class Foo" }, chunk.ContextHeaders);
        Assert.Equal(new[] { "void Bar()", "    return 1;" }, chunk.OldLines);
        Assert.Equal(new[] { "void Bar()", "    return 2;" }, chunk.NewLines);
    }

    [Fact]
    public void Parse_UpdateFile_WithMove()
    {
        var patch = """
            *** Begin Patch
            *** Update File: old/path.cs
            *** Move to: new/path.cs
             context
            +added
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var upd = Assert.IsType<UpdateFile>(Assert.Single(ops));
        Assert.Equal("new/path.cs", upd.MoveTo);
    }

    [Fact]
    public void Parse_MultipleOperations_InOrder()
    {
        var patch = """
            *** Begin Patch
            *** Add File: a.txt
            +a
            *** Delete File: b.txt
            *** Update File: c.txt
             x
            +y
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        Assert.Equal(3, ops.Count);
        Assert.IsType<AddFile>(ops[0]);
        Assert.IsType<DeleteFile>(ops[1]);
        Assert.IsType<UpdateFile>(ops[2]);
    }

    [Fact]
    public void Parse_EndOfFileMarker_SetsFlag()
    {
        var patch = """
            *** Begin Patch
            *** Update File: a.txt
             last line
            +appended
            *** End of File
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var upd = Assert.IsType<UpdateFile>(Assert.Single(ops));
        Assert.True(Assert.Single(upd.Chunks).IsEndOfFile);
    }

    [Fact]
    public void Parse_MissingBeginPatch_Throws()
    {
        Assert.Throws<PatchParseException>(() => PatchParser.Parse("*** Add File: a\n+x\n*** End Patch"));
    }

    [Fact]
    public void Parse_MissingEndPatch_Throws()
    {
        Assert.Throws<PatchParseException>(() => PatchParser.Parse("*** Begin Patch\n*** Add File: a\n+x\n"));
    }

    [Fact]
    public void Parse_MultipleContextHeaders_CollectedInOrder()
    {
        var patch = """
            *** Begin Patch
            *** Update File: a.cs
            @@ class Outer
            @@ class Inner
             method()
            -    old;
            +    new;
            *** End Patch
            """;

        var ops = PatchParser.Parse(patch);
        var upd = Assert.IsType<UpdateFile>(Assert.Single(ops));
        Assert.Equal(new[] { "class Outer", "class Inner" }, Assert.Single(upd.Chunks).ContextHeaders);
    }

    [Fact]
    public void Parse_CrlfLineEndings_NormalizedBeforeParse()
    {
        var patch = "*** Begin Patch\r\n*** Add File: a.txt\r\n+hi\r\n*** End Patch\r\n";
        var ops = PatchParser.Parse(patch);
        var add = Assert.IsType<AddFile>(Assert.Single(ops));
        Assert.Equal("hi", add.Content);
    }
}
