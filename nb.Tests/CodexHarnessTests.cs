using nb.Harness;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The codex costume. Its distinguishing property is subtraction: Codex has no read,
/// write, edit, glob, grep or list tool, so most of what this costume does is withhold
/// tools nb holds and the model would otherwise reach for.
/// </summary>
public class CodexHarnessTests : IDisposable
{
    private readonly string _dir;
    private readonly ShellEnvironment _env;
    private readonly CodexHarness _harness;

    public CodexHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-codex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_dir);
        _harness = new CodexHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120), new ReadFileTool(_env),
            new WriteFileTool(_env), new EditFileTool(_env), new FindFilesTool(_env),
            new GrepTool(_env), new ListDirTool(_env), fetchUrl: null, searchWeb: null,
            applyPatch: new ApplyPatchTool(_env));
        _harness.Configure(new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false),
            trustMode: true, verbose: false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Advertises_CodexsFourTools()
    {
        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.Equal(new[] { "shell_command", "apply_patch", "update_plan", "view_image" }, advertised);
    }

    /// <summary>
    /// The rule from plans/harness-emulation.md: a costume must not advertise a tool its
    /// target does not have. Codex's answer to "read a file" is `sed -n` through the
    /// shell, and offering read_file alongside would change what the model does.
    /// </summary>
    [Fact]
    public void Withholds_TheFileAndSearchToolsCodexDoesNotHave()
    {
        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        foreach (var absent in new[] { "read_file", "write_file", "edit_file", "edit",
                                       "find_files", "glob", "grep", "list_dir", "todo_write",
                                       "todo_read", "fetch_url", "search_web" })
            Assert.DoesNotContain(absent, advertised);
    }

    /// <summary>A tools directive still filters a costume, in nb's canonical vocabulary.</summary>
    [Fact]
    public void ToolsDirective_FiltersInCanonicalVocabulary()
    {
        var surface = new ToolSurface
        {
            NativeAllow = new HashSet<string>(new[] { "bash", "apply_patch" }, StringComparer.OrdinalIgnoreCase),
        };

        var advertised = _harness.CreateTools(surface).Select(t => t.Name).ToList();

        Assert.Equal(new[] { "shell_command", "apply_patch" }, advertised);
    }

    [Fact]
    public void Preamble_IsCodexsOwnPromptWithItsProvenanceStripped()
    {
        var preamble = _harness.Preamble;

        Assert.False(string.IsNullOrWhiteSpace(preamble),
            $"prompts/harness/codex.md did not load from {AppContext.BaseDirectory}");
        Assert.StartsWith("You are a coding agent running in the Codex CLI", preamble);
        Assert.DoesNotContain("<!--", preamble);
        Assert.DoesNotContain("VENDORED", preamble);
    }

    /// <summary>
    /// The two stated Apache-2.0 modifications, asserted so an unreviewed re-vendor of
    /// upstream cannot quietly undo them. The FREEFORM clause in particular would steer
    /// the model into emitting a bare patch where nb requires an argument object.
    /// </summary>
    [Fact]
    public void Preamble_CarriesTheStatedModifications()
    {
        var preamble = _harness.Preamble!;

        Assert.DoesNotContain("FREEFORM", preamble);
        Assert.DoesNotContain("You are GPT-5.2", preamble);
        Assert.Contains("*** Begin Patch", preamble);   // the patch grammar survives
        Assert.Contains("update_plan", preamble);
    }

    [Fact]
    public void Costume_DeclaresTheVendoringAndTheMissingAgentsFile()
    {
        var omissions = _harness.Omissions;

        Assert.Contains(omissions, o => o.StartsWith("system prompt:") && o.Contains("vendored"));
        Assert.Contains(omissions, o => o.StartsWith("AGENTS.md:"));
    }

    // ---- dispatch ----

    [Fact]
    public async Task ShellCommand_RunsThroughNbsBash()
    {
        var outcome = await _harness.InvokeAsync("shell_command", "c1",
            new Dictionary<string, object?> { ["command"] = "echo hello-from-codex" });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError);
        Assert.Contains("hello-from-codex", outcome.Value.Content.Result?.ToString());
    }

    /// <summary>
    /// Codex has no read tool, so nb's read-before-edit rule would refuse every patch the
    /// costume can produce — the model reads with `sed` through the shell, which the
    /// tracker cannot see. This is the assertion that the costume is usable at all.
    /// </summary>
    [Fact]
    public async Task ApplyPatch_EditsAFileThatWasNeverReadThroughATool()
    {
        var file = Path.Combine(_dir, "hello.txt");
        await File.WriteAllTextAsync(file, "one\ntwo\nthree\n");

        var patch = "*** Begin Patch\n*** Update File: hello.txt\n@@\n one\n-two\n+TWO\n three\n*** End Patch";
        var outcome = await _harness.InvokeAsync("apply_patch", "c2",
            new Dictionary<string, object?> { ["input"] = patch });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError, outcome.Value.Content.Result?.ToString());
        Assert.Equal("one\nTWO\nthree\n", (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n"));
    }

    /// <summary>nb's own harness keeps the guard — the relaxation is the costume's, not the engine's.</summary>
    [Fact]
    public void ReadBeforeEdit_StaysOnForNbsOwnSurface()
    {
        Assert.True(new NbHarness().Files.RequireReadBeforeEdit);
        Assert.False(_harness.Files.RequireReadBeforeEdit);
    }

    [Fact]
    public async Task UpdatePlan_BecomesTheTodoList()
    {
        await _harness.InvokeAsync("update_plan", "c3", Plan(
            ("Read the failing test", "completed"),
            ("Fix the parser", "in_progress"),
            ("Run the suite", "pending")));

        Assert.Equal(
            new[] { "Read the failing test", "Fix the parser", "Run the suite" },
            _harness.Todos.GetAll().Select(t => t.Content));
        Assert.Equal(TodoStatus.InProgress,
            _harness.Todos.GetAll().Single(t => t.Content == "Fix the parser").Status);
    }

    /// <summary>
    /// update_plan replaces the plan; nb's todo list merges by content. A step the new
    /// plan drops has to go, or a costume that reports "3 steps" would render five.
    /// </summary>
    [Fact]
    public async Task UpdatePlan_ReplacesTheOldPlanRatherThanMergingIntoIt()
    {
        await _harness.InvokeAsync("update_plan", "c4", Plan(
            ("Investigate", "in_progress"), ("Abandoned idea", "pending")));

        await _harness.InvokeAsync("update_plan", "c5", Plan(
            ("Investigate", "completed"), ("Ship it", "in_progress")));

        Assert.Equal(new[] { "Investigate", "Ship it" }, _harness.Todos.GetAll().Select(t => t.Content));
    }

    [Fact]
    public async Task ViewImage_ReadsThroughNbsReadFileTool()
    {
        var file = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(file, "not an image, but readable");

        var outcome = await _harness.InvokeAsync("view_image", "c6",
            new Dictionary<string, object?> { ["path"] = "notes.txt" });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError);
    }

    /// <summary>A name this costume does not advertise is not the harness's to run.</summary>
    [Fact]
    public async Task UnknownName_FallsThroughToTheCaller()
    {
        Assert.Null(await _harness.InvokeAsync("read_file", "c7",
            new Dictionary<string, object?> { ["path"] = "notes.txt" }));
    }

    private static Dictionary<string, object?> Plan(params (string Step, string Status)[] steps) =>
        new()
        {
            ["plan"] = System.Text.Json.JsonSerializer.Serialize(
                steps.Select(s => new { step = s.Step, status = s.Status })),
        };
}
