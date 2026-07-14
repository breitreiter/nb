using nb.Transcript;

namespace nb.Tests;

public class ToolSurfaceTests
{
    private static readonly string[] Native = { "bash", "read_file", "grep" };

    [Fact]
    public void NoDirectives_NativeAllOn_McpStrictEmpty()
    {
        var s = ToolSurface.Fold(Array.Empty<SurfaceDirectiveEvent>(), Native);

        Assert.Null(s.NativeAllow);          // null => all native tools on
        Assert.True(s.AllowsNative("bash"));
        Assert.Empty(s.McpServers!);         // strict-empty baseline: no MCP for a program
    }

    [Fact]
    public void ToolsNone_ClearsNative()
    {
        var s = ToolSurface.Fold(new SurfaceDirectiveEvent[] { new ToolsEvent { Reset = true } }, Native);

        Assert.NotNull(s.NativeAllow);
        Assert.Empty(s.NativeAllow!);
        Assert.False(s.AllowsNative("bash"));
    }

    [Fact]
    public void ToolsRemove_DropsOneKeepsRest()
    {
        var s = ToolSurface.Fold(new SurfaceDirectiveEvent[] { new ToolsEvent { Remove = new[] { "bash" } } }, Native);

        Assert.False(s.AllowsNative("bash"));
        Assert.True(s.AllowsNative("read_file"));
        Assert.True(s.AllowsNative("grep"));
    }

    [Fact]
    public void ToolsNoneThenAdd_ReAddsOnlyNamed()
    {
        var s = ToolSurface.Fold(
            new SurfaceDirectiveEvent[] { new ToolsEvent { Reset = true }, new ToolsEvent { Add = new[] { "bash" } } },
            Native);

        Assert.True(s.AllowsNative("bash"));
        Assert.False(s.AllowsNative("grep"));
    }

    [Fact]
    public void McpAdd_ProducesAllowList()
    {
        var s = ToolSurface.Fold(new SurfaceDirectiveEvent[] { new McpEvent { Add = new[] { "tester" } } }, Native);

        Assert.Equal(new[] { "tester" }, s.McpServers);
    }

    [Fact]
    public void McpAddThenRemove_NetsEmpty()
    {
        var s = ToolSurface.Fold(
            new SurfaceDirectiveEvent[] { new McpEvent { Add = new[] { "tester", "figma" } }, new McpEvent { Remove = new[] { "tester" } } },
            Native);

        Assert.Equal(new[] { "figma" }, s.McpServers);
    }

    [Fact]
    public void McpAdd_IsIdempotent()
    {
        var s = ToolSurface.Fold(
            new SurfaceDirectiveEvent[] { new McpEvent { Add = new[] { "tester" } }, new McpEvent { Add = new[] { "tester" } } },
            Native);

        Assert.Single(s.McpServers!);
    }

    [Fact]
    public void All_IsUncontrolled()
    {
        // The bare/-p default (never folded): null MCP => all connected servers.
        Assert.Null(ToolSurface.All.NativeAllow);
        Assert.Null(ToolSurface.All.McpServers);
        Assert.True(ToolSurface.All.AllowsNative("anything"));
    }
}
