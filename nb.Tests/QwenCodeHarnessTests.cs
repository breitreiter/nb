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
        _harness.Configure(new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false),
            trustMode: true, verbose: false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // Dispatch is the adaptation now — there is no translation layer to unit-test, so
    // these drive the costume's own tool names and argument spellings all the way to a
    // real file and assert the effect.

    [Fact]
    public async Task Edit_UnpacksFilePathAndAppliesTheChange()
    {
        var file = Path.Combine(_dir, "sample.txt");
        await File.WriteAllTextAsync(file, "alpha\nbeta\n");

        // read-before-edit is enforced, so read it the way the costume would.
        await _harness.InvokeAsync("read_file", "c0", new Dictionary<string, object?> { ["file_path"] = file });

        var outcome = await _harness.InvokeAsync("edit", "c1", new Dictionary<string, object?>
        {
            ["file_path"] = file,
            ["old_string"] = "beta",
            ["new_string"] = "gamma",
        });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError);
        Assert.Equal("alpha\ngamma\n", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task GrepSearch_UnpacksGlobAndLimit()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.cs"), "// TODO one\n");
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.txt"), "// TODO two\n");

        var outcome = await _harness.InvokeAsync("grep_search", "c1", new Dictionary<string, object?>
        {
            ["pattern"] = "TODO",
            ["glob"] = "*.cs",
            ["limit"] = 10,
        });

        Assert.NotNull(outcome);
        var text = outcome!.Value.Content.Result?.ToString() ?? "";
        Assert.Contains("a.cs", text);
        Assert.DoesNotContain("b.txt", text);
    }

    [Fact]
    public async Task Glob_UnpacksPattern()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "x.cs"), "");
        await File.WriteAllTextAsync(Path.Combine(_dir, "y.md"), "");

        var outcome = await _harness.InvokeAsync("glob", "c1",
            new Dictionary<string, object?> { ["pattern"] = "*.cs" });

        var text = outcome!.Value.Content.Result?.ToString() ?? "";
        Assert.Contains("x.cs", text);
        Assert.DoesNotContain("y.md", text);
    }

    /// <summary>Arguments nb has no equivalent for are accepted and ignored, not fatal.</summary>
    [Fact]
    public async Task UnsupportedArguments_AreIgnored()
    {
        var outcome = await _harness.InvokeAsync("list_directory", "c1", new Dictionary<string, object?>
        {
            ["path"] = _dir,
            ["ignore"] = new[] { "*.tmp" },
            ["file_filtering_options"] = "whatever",
        });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError);
    }

    /// <summary>
    /// nb's own tool names are NOT part of this costume, so dispatching one must decline
    /// rather than quietly work — otherwise the surface the model sees and the surface it
    /// can actually reach would differ.
    /// </summary>
    [Theory]
    [InlineData("edit_file")]
    [InlineData("find_files")]
    [InlineData("list_dir")]
    [InlineData("bash")]
    [InlineData("some_mcp_tool")]
    public async Task NamesOutsideTheCostume_AreDeclined(string name)
    {
        Assert.Null(await _harness.InvokeAsync(name, "c1", new Dictionary<string, object?>()));
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("glob")]
    [InlineData("grep_search")]
    [InlineData("list_directory")]
    [InlineData("read_file")]
    [InlineData("write_file")]
    public async Task EveryAdvertisedName_Dispatches(string name)
    {
        // Empty arguments: the call may well fail, but it must be *handled* (non-null),
        // which is what proves the advertised name reaches an implementation.
        Assert.NotNull(await _harness.InvokeAsync(name, "c1", new Dictionary<string, object?>()));
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
    /// A costume that ADDS tools its target does not have is as unfaithful as one that
    /// drops tools it does have — the goal is a model behaving as though it were in
    /// qwen-code, not a model with a bigger toolbox. Every advertised name must be a real
    /// qwen-code tool name (packages/core/src/tools/tool-names.ts).
    /// </summary>
    [Fact]
    public void Costume_AdvertisesNothingQwenCodeDoesNotHave()
    {
        // Verified against QwenLM/qwen-code tool-names.ts.
        var qwenCodeToolNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "edit", "write_file", "read_file", "grep_search", "glob", "run_shell_command",
            "todo_write", "web_fetch", "web_search", "list_directory",
        };

        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.NotEmpty(advertised);
        Assert.All(advertised, name => Assert.Contains(name, qwenCodeToolNames));
    }

    /// <summary>
    /// apply_patch has no qwen-code counterpart, so it is not advertised even though the
    /// runtime always builds one — and the costume keeps its own edit tools regardless,
    /// which is what stops <c>EditToolStyle: ApplyPatch</c> from reaching in and leaving
    /// it with no way to edit a file.
    /// </summary>
    [Fact]
    public void ApplyPatch_IsNeverAdvertised_AndTheEditToolsSurviveAnyway()
    {
        var env = ShellEnvironment.Detect();
        env.SetCwd(_dir);
        var withApplyPatch = new QwenCodeHarness(
            new BashTool(env, defaultTimeoutSeconds: 120), new ReadFileTool(env),
            new WriteFileTool(env), new EditFileTool(env), findFiles: null, grep: null,
            listDir: null, fetchUrl: null, searchWeb: null, applyPatch: new ApplyPatchTool(env));

        var advertised = withApplyPatch.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.DoesNotContain("apply_patch", advertised);
        Assert.Contains("write_file", advertised);
        Assert.Contains("edit", advertised);
        Assert.DoesNotContain(withApplyPatch.Omissions, o => o.StartsWith("CONFLICT:"));
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
