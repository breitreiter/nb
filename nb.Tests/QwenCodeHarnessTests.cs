using nb.Harness;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The qwen-code costume translates between the surface the model was trained on and
/// nb's canonical vocabulary. The advertised shape is pinned by the goldens in
/// <see cref="ToolSurfaceGoldenTests"/>; this covers the inbound translation, which is
/// what makes a costume dispatch at all.
/// </summary>
public class QwenCodeHarnessTests : IDisposable
{
    private readonly string _dir;
    private readonly QwenCodeHarness _harness;

    public QwenCodeHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-qwen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        var env = ShellEnvironment.Detect();
        env.SetCwd(_dir);

        _harness = new QwenCodeHarness(
            new BashTool(env, defaultTimeoutSeconds: 120), new ReadFileTool(env), new WriteFileTool(env),
            new EditFileTool(env), new FindFilesTool(env), new GrepTool(env), new ListDirTool(env),
            new FetchUrlTool());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("edit", "edit_file")]
    [InlineData("glob", "find_files")]
    [InlineData("grep_search", "grep")]
    [InlineData("list_directory", "list_dir")]
    [InlineData("run_shell_command", "bash")]
    [InlineData("web_fetch", "fetch_url")]
    [InlineData("web_search", "search_web")]
    [InlineData("read_file", "read_file")]
    [InlineData("write_file", "write_file")]
    public void WireNames_TranslateToCanonical(string wire, string canonical)
    {
        var (name, _) = _harness.ToCanonical(wire, new Dictionary<string, object?>());
        Assert.Equal(canonical, name);
        Assert.Equal(wire, _harness.ToWireName(canonical));
    }

    /// <summary>
    /// The sharpest mismatch in the whole costume, and the one the measured failure sits
    /// on: a model emitting file_path against nb's schema produces an empty path, which
    /// presents as the model being stupid rather than as a schema mismatch.
    /// </summary>
    [Fact]
    public void FilePath_TranslatesToPath()
    {
        var (name, args) = _harness.ToCanonical("edit", new Dictionary<string, object?>
        {
            ["file_path"] = "/tmp/x.cs",
            ["old_string"] = "a",
            ["new_string"] = "b",
        });

        Assert.Equal("edit_file", name);
        Assert.Equal("/tmp/x.cs", args!["path"]);
        Assert.False(args.ContainsKey("file_path"));
        Assert.Equal("a", args["old_string"]);
    }

    [Fact]
    public void GrepSearch_RenamesGlobAndLimit()
    {
        var (_, args) = _harness.ToCanonical("grep_search", new Dictionary<string, object?>
        {
            ["pattern"] = "TODO",
            ["glob"] = "*.cs",
            ["limit"] = 20,
        });

        Assert.Equal("*.cs", args!["file_pattern"]);
        Assert.Equal(20, args["max_results"]);
        Assert.Equal("TODO", args["pattern"]);
    }

    /// <summary>qwen-code's shell timeout is milliseconds; nb's bash takes seconds.</summary>
    [Theory]
    [InlineData(30000, 30)]
    [InlineData(1500, 2)]
    [InlineData(100, 1)]   // floors at one second rather than zero
    public void ShellTimeout_ConvertsMillisecondsToSeconds(int ms, int expectedSeconds)
    {
        var (_, args) = _harness.ToCanonical("run_shell_command", new Dictionary<string, object?>
        {
            ["command"] = "ls",
            ["timeout"] = ms,
        });

        Assert.Equal(expectedSeconds, args!["timeout_seconds"]);
    }

    /// <summary>Arguments nb has no equivalent for are accepted and dropped, not passed through.</summary>
    [Fact]
    public void UnsupportedArguments_AreDropped()
    {
        var (_, args) = _harness.ToCanonical("run_shell_command", new Dictionary<string, object?>
        {
            ["command"] = "ls",
            ["is_background"] = false,
            ["directory"] = "/elsewhere",
        });

        Assert.Equal("ls", args!["command"]);
        Assert.False(args.ContainsKey("is_background"));
        Assert.False(args.ContainsKey("directory"));
    }

    /// <summary>MCP and fake tools are not part of a costume and must pass through untouched.</summary>
    [Fact]
    public void ForeignToolNames_PassThrough()
    {
        var args = new Dictionary<string, object?> { ["file_path"] = "keep me" };
        var (name, translated) = _harness.ToCanonical("some_mcp_tool", args);

        Assert.Equal("some_mcp_tool", name);
        Assert.Same(args, translated);
        Assert.Equal("some_mcp_tool", _harness.ToWireName("some_mcp_tool"));
    }

    [Fact]
    public void Costume_DeclaresItsOmissions()
    {
        Assert.NotEmpty(_harness.Omissions);
        Assert.Contains(_harness.Omissions, o => o.Contains("system prompt"));
        Assert.Empty(new NbHarness().Omissions);
    }

    /// <summary>
    /// qwen-code declares todo_write with no read counterpart, so the costume drops
    /// todo_read — fidelity, and the kind of difference the goldens are there to pin.
    /// </summary>
    [Fact]
    public void Costume_DropsTodoRead()
    {
        var names = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.Contains("todo_write", names);
        Assert.DoesNotContain("todo_read", names);
    }

    /// <summary>
    /// A tools directive filters in nb's canonical vocabulary, not the costume's — so a
    /// program reads the same whichever harness it wears. See the open question in
    /// plans/harness-emulation.md.
    /// </summary>
    [Fact]
    public void ToolsDirective_FiltersInCanonicalVocabulary()
    {
        var surface = new ToolSurface
        {
            NativeAllow = new HashSet<string>(new[] { "edit_file" }, StringComparer.OrdinalIgnoreCase),
        };

        Assert.Equal(new[] { "edit" }, _harness.CreateTools(surface).Select(t => t.Name));
    }
}
