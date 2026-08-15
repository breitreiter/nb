using nb.Harness;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The claude-code costume. The two halves come from different places and are asserted
/// differently: the surface is reproduced exactly and pinned by name and spelling, while
/// the prompt is an nb-authored facsimile whose only testable properties are that it
/// loaded, that it says what it is, and that it is not a transcription.
/// </summary>
public class ClaudeCodeHarnessTests : IDisposable
{
    private readonly string _dir;
    private readonly ShellEnvironment _env;
    private readonly ClaudeCodeHarness _harness;

    public ClaudeCodeHarnessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-cc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_dir);
        _harness = new ClaudeCodeHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120), new ReadFileTool(_env),
            new WriteFileTool(_env), new EditFileTool(_env), new FindFilesTool(_env),
            new GrepTool(_env), new ListDirTool(_env), new FetchUrlTool(), searchWeb: null,
            applyPatch: new ApplyPatchTool(_env));
        _harness.Configure(new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false), verbose: false);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Advertises_ClaudeCodesNamesInClaudeCodesOrder()
    {
        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.Equal(
            new[] { "Task", "Bash", "Glob", "Grep", "Read", "Edit", "Write", "NotebookEdit",
                    "WebFetch", "TodoWrite", "Skill" },
            advertised);
    }

    /// <summary>
    /// PascalCase and file_path are the whole point of the surface half — nb's own
    /// vocabulary must not leak through.
    /// </summary>
    [Fact]
    public void Advertises_NoneOfNbsOwnToolNames()
    {
        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        foreach (var canonical in new[] { "bash", "read_file", "write_file", "edit_file",
                                          "find_files", "grep", "list_dir", "apply_patch",
                                          "fetch_url", "search_web", "todo_write", "todo_read" })
            Assert.DoesNotContain(canonical, advertised);
    }

    /// <summary>
    /// Claude Code has no list-directory tool and no patch applier — it lists with Bash
    /// and edits with Edit. Both exist on this harness and are deliberately withheld.
    /// </summary>
    [Fact]
    public void Withholds_TheToolsClaudeCodeDoesNotHave()
    {
        var advertised = _harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.DoesNotContain("ListDir", advertised);
        Assert.DoesNotContain("ApplyPatch", advertised);
        Assert.DoesNotContain("BashOutput", advertised);
        Assert.DoesNotContain("KillShell", advertised);
        Assert.DoesNotContain("MultiEdit", advertised);
    }

    [Fact]
    public void Read_UsesFilePathNotPath()
    {
        var read = _harness.CreateTools(ToolSurface.All).Single(t => t.Name == "Read");

        var schema = read.JsonSchema.GetProperty("properties");
        Assert.True(schema.TryGetProperty("file_path", out _));
        Assert.False(schema.TryGetProperty("path", out _));
    }

    [Fact]
    public void Grep_DeclaresTheRipgrepShapedParameters()
    {
        var grep = _harness.CreateTools(ToolSurface.All).Single(t => t.Name == "Grep");

        var props = grep.JsonSchema.GetProperty("properties");
        foreach (var p in new[] { "pattern", "path", "glob", "type", "output_mode", "-i", "-n",
                                  "-A", "-B", "-C", "head_limit", "multiline" })
            Assert.True(props.TryGetProperty(p, out _), $"Grep is missing {p}");
    }

    /// <summary>
    /// The settled vocabulary rule (plans/harness-emulation.md, "Vocabulary", decided
    /// 2026-08-15): a program writes `tools -edit_file` even under a costume that
    /// advertises `Edit`, because the directive states what the run may do rather than
    /// what the model is shown. Writing `Edit` is now a validation error, not a no-op.
    /// </summary>
    [Fact]
    public void ToolsDirective_FiltersInCanonicalVocabulary()
    {
        var surface = new ToolSurface
        {
            NativeAllow = new HashSet<string>(new[] { "read_file", "edit_file" }, StringComparer.OrdinalIgnoreCase),
        };

        var advertised = _harness.CreateTools(surface).Select(t => t.Name).ToList();

        Assert.Contains("Read", advertised);
        Assert.Contains("Edit", advertised);
        Assert.DoesNotContain("Bash", advertised);
        Assert.DoesNotContain("Grep", advertised);
    }

    /// <summary>
    /// The declared-but-unbacked tools have no canonical counterpart to filter on, so they
    /// ride the surface as a group — present by default, gone under `tools none`.
    /// </summary>
    [Fact]
    public void ToolsNone_TakesTheDeclaredStubsWithIt()
    {
        var advertised = _harness
            .CreateTools(new ToolSurface { NativeAllow = new HashSet<string>() })
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(advertised);
    }

    // ---- the prompt half ----

    [Fact]
    public void Preamble_LoadsAndDropsItsProvenanceComment()
    {
        var preamble = _harness.Preamble;

        Assert.False(string.IsNullOrWhiteSpace(preamble),
            $"prompts/harness/claude-code.md did not load from {AppContext.BaseDirectory}");
        Assert.StartsWith("You are an interactive CLI tool", preamble);
        Assert.DoesNotContain("<!--", preamble);
        Assert.DoesNotContain("NOT VENDORED", preamble);
    }

    /// <summary>
    /// The costume must not imply fidelity it does not have, and must say that the prompt
    /// was authored rather than transcribed — this is the costume with the worst prompt
    /// provenance and the strongest reason to be explicit about it.
    /// </summary>
    [Fact]
    public void Costume_DeclaresThePromptIsAFacsimile()
    {
        var prompt = Assert.Single(_harness.Omissions, o => o.StartsWith("system prompt:"));

        Assert.Contains("facsimile", prompt);
        Assert.Contains("not transcribed", prompt);
    }

    [Fact]
    public void Costume_DeclaresTheUnbackedTools()
    {
        Assert.Contains(_harness.Omissions, o => o.StartsWith("Task / Skill / NotebookEdit:"));
        Assert.Contains(_harness.Omissions, o => o.StartsWith("BashOutput / KillShell:"));
    }

    // ---- CLAUDE.md ----

    [Fact]
    public void ProjectInstructions_UseTheSystemReminderWrapper()
    {
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "Use tabs.");

        var instructions = _harness.ProjectInstructions()!;

        Assert.StartsWith("<system-reminder>", instructions);
        Assert.EndsWith("</system-reminder>", instructions);
        Assert.Contains("(project instructions, checked into the codebase)", instructions);
        Assert.Contains("Use tabs.", instructions);
    }

    [Fact]
    public void ProjectInstructions_AreNullWithoutAClaudeMd()
    {
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "This is the other harness's file.");

        Assert.Null(_harness.ProjectInstructions());
    }

    // ---- dispatch ----

    [Fact]
    public async Task Read_ThenEdit_RoundTripsThroughNbsTools()
    {
        var file = Path.Combine(_dir, "greeting.txt");
        await File.WriteAllTextAsync(file, "hello world\n");

        var read = await _harness.InvokeAsync("Read", "c1",
            new Dictionary<string, object?> { ["file_path"] = file });
        Assert.NotNull(read);
        Assert.False(read!.Value.IsError);

        var edit = await _harness.InvokeAsync("Edit", "c2", new Dictionary<string, object?>
        {
            ["file_path"] = file,
            ["old_string"] = "world",
            ["new_string"] = "there",
        });

        Assert.NotNull(edit);
        Assert.False(edit!.Value.IsError, edit.Value.Content.Result?.ToString());
        Assert.Equal("hello there\n", (await File.ReadAllTextAsync(file)).Replace("\r\n", "\n"));
    }

    /// <summary>Claude Code keeps nb's read-before-edit rule: it has a Read tool of its own.</summary>
    [Fact]
    public void ReadBeforeEdit_StaysOn()
    {
        Assert.True(_harness.Files.RequireReadBeforeEdit);
    }

    [Fact]
    public async Task Grep_MapsRipgrepFlagsOntoNbsGrep()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.txt"), "Needle in here\n");

        var outcome = await _harness.InvokeAsync("Grep", "c3", new Dictionary<string, object?>
        {
            ["pattern"] = "needle",
            ["-i"] = true,
            ["output_mode"] = "files_with_matches",
        });

        Assert.NotNull(outcome);
        Assert.False(outcome!.Value.IsError);
        Assert.Contains("a.txt", outcome.Value.Content.Result?.ToString());
    }

    [Fact]
    public async Task TodoWrite_ReplacesTheListRatherThanMergingIntoIt()
    {
        await _harness.InvokeAsync("TodoWrite", "c4", Todos(
            ("Investigate", "in_progress"), ("Abandoned idea", "pending")));

        await _harness.InvokeAsync("TodoWrite", "c5", Todos(
            ("Investigate", "completed"), ("Ship it", "in_progress")));

        Assert.Equal(new[] { "Investigate", "Ship it" }, _harness.Todos.GetAll().Select(t => t.Content));
    }

    /// <summary>
    /// An unbacked tool reports that it did nothing rather than returning a plausible
    /// fake — a fabricated answer in the transcript would make the run silently
    /// meaningless, which is worse than a visible failure.
    /// </summary>
    [Fact]
    public async Task DeclaredButUnbackedTools_SaySoInsteadOfFakingAResult()
    {
        foreach (var name in new[] { "Task", "Skill", "NotebookEdit" })
        {
            var outcome = await _harness.InvokeAsync(name, "c6", new Dictionary<string, object?>());

            Assert.NotNull(outcome);
            Assert.True(outcome!.Value.IsError);
            Assert.Contains("not implemented", outcome.Value.Content.Result?.ToString());
        }
    }

    [Fact]
    public async Task UnknownName_FallsThroughToTheCaller()
    {
        Assert.Null(await _harness.InvokeAsync("read_file", "c7",
            new Dictionary<string, object?> { ["path"] = "greeting.txt" }));
    }

    private static Dictionary<string, object?> Todos(params (string Content, string Status)[] items) =>
        new()
        {
            ["todos"] = System.Text.Json.JsonSerializer.Serialize(
                items.Select(i => new { content = i.Content, status = i.Status, activeForm = i.Content })),
        };
}
