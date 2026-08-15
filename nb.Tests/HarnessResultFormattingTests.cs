using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// What a tool call *returns* is the observation half of the loop, and costumes own it —
/// ranked second in plans/harness-emulation.md, behind only the advertised surface.
///
/// Two jobs here. The nb cases are a regression guard: those exact strings were inline in
/// the capability methods before the formatters were extracted, and nothing else asserts
/// them. The costume cases pin the overrides.
/// </summary>
public class HarnessResultFormattingTests : IDisposable
{
    private readonly string _dir;
    private readonly ShellEnvironment _env;

    public HarnessResultFormattingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-fmt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- nb's own surface: unchanged by the extraction ----

    [Fact]
    public async Task Nb_ShellResult_KeepsItsLabelledStreamsAndExitCodeFooter()
    {
        var outcome = await Harness<NbHarness>().InvokeAsync("bash", "c1",
            new Dictionary<string, object?> { ["command"] = "echo hello" });

        Assert.Equal("hello\n\n[exit code: 0]", Text(outcome));
    }

    [Fact]
    public async Task Nb_EditAndWrite_KeepTheirSuccessfullyStrings()
    {
        var harness = Harness<NbHarness>();
        var file = Path.Combine(_dir, "f.txt");

        var write = await harness.InvokeAsync("write_file", "c2",
            new Dictionary<string, object?> { ["path"] = file, ["content"] = "one\ntwo\n" });
        Assert.StartsWith("Successfully wrote ", Text(write));
        Assert.EndsWith($" bytes to {file}", Text(write));

        var edit = await harness.InvokeAsync("edit_file", "c3", new Dictionary<string, object?>
        {
            ["path"] = file,
            ["old_string"] = "two",
            ["new_string"] = "three",
        });
        Assert.Equal($"Successfully edited {file} (1 replacement)", Text(edit));
    }

    [Fact]
    public async Task Nb_ApplyPatch_KeepsItsCountSummary()
    {
        var outcome = await Harness<NbHarness>().InvokeAsync("apply_patch", "c4",
            new Dictionary<string, object?> { ["input"] = AddFilePatch("new.txt") });

        Assert.Equal("Applied patch: 1 added, 0 updated, 0 deleted", Text(outcome));
    }

    // ---- codex: verified against openai/codex ----

    /// <summary>
    /// format_exec_output_for_model leads with the exit code and labels the body, where
    /// nb trails a footer — the model reads the status before the output, not after it.
    /// </summary>
    [Fact]
    public async Task Codex_ShellResult_LeadsWithTheExitCode()
    {
        var outcome = await Harness<CodexHarness>().InvokeAsync("shell_command", "c5",
            new Dictionary<string, object?> { ["command"] = "echo hello" });

        Assert.Equal("Exit code: 0\nOutput:\nhello", Text(outcome));
    }

    /// <summary>print_summary in codex-rs/apply-patch/src/lib.rs: a status letter per path.</summary>
    [Fact]
    public async Task Codex_ApplyPatch_UsesTheGitStyleSummary()
    {
        var outcome = await Harness<CodexHarness>().InvokeAsync("apply_patch", "c6",
            new Dictionary<string, object?> { ["input"] = AddFilePatch("added.txt") });

        var text = Text(outcome);
        Assert.StartsWith("Success. Updated the following files:\n", text);
        Assert.Contains("A ", text);
        Assert.Contains("added.txt", text);
        Assert.DoesNotContain("Applied patch:", text);
    }

    // ---- claude-code: written from observed behaviour ----

    /// <summary>
    /// A quiet successful command returns nothing at all, where nb returns a bare
    /// exit-code footer. That difference is what the model reads as "that worked".
    /// </summary>
    [Fact]
    public async Task ClaudeCode_ShellResult_IsRawWithNoFooterOnSuccess()
    {
        var harness = Harness<ClaudeCodeHarness>();

        var loud = await harness.InvokeAsync("Bash", "c7",
            new Dictionary<string, object?> { ["command"] = "echo hello" });
        Assert.Equal("hello", Text(loud));

        var quiet = await harness.InvokeAsync("Bash", "c8",
            new Dictionary<string, object?> { ["command"] = "true" });
        Assert.Equal("", Text(quiet));
    }

    [Fact]
    public async Task ClaudeCode_ShellResult_ShowsTheExitCodeOnlyWhenItFails()
    {
        var outcome = await Harness<ClaudeCodeHarness>().InvokeAsync("Bash", "c9",
            new Dictionary<string, object?> { ["command"] = "exit 3" });

        Assert.Equal("Exit code 3", Text(outcome));
    }

    [Fact]
    public async Task ClaudeCode_Write_DistinguishesCreatingFromOverwriting()
    {
        var harness = Harness<ClaudeCodeHarness>();
        var file = Path.Combine(_dir, "note.txt");

        var created = await harness.InvokeAsync("Write", "c10",
            new Dictionary<string, object?> { ["file_path"] = file, ["content"] = "first\n" });
        Assert.Equal($"File created successfully at: {file}", Text(created));

        // Overwriting an existing file requires reading it first — nb's guard, unchanged.
        await harness.InvokeAsync("Read", "c11", new Dictionary<string, object?> { ["file_path"] = file });

        var updated = await harness.InvokeAsync("Write", "c12",
            new Dictionary<string, object?> { ["file_path"] = file, ["content"] = "second\n" });
        Assert.Equal($"The file {file} has been updated.", Text(updated));
    }

    [Fact]
    public async Task ClaudeCode_Edit_AcknowledgesByPath()
    {
        var harness = Harness<ClaudeCodeHarness>();
        var file = Path.Combine(_dir, "edit-me.txt");
        await File.WriteAllTextAsync(file, "alpha\n");
        await harness.InvokeAsync("Read", "c13", new Dictionary<string, object?> { ["file_path"] = file });

        var outcome = await harness.InvokeAsync("Edit", "c14", new Dictionary<string, object?>
        {
            ["file_path"] = file,
            ["old_string"] = "alpha",
            ["new_string"] = "beta",
        });

        Assert.Equal($"The file {file} has been updated.", Text(outcome));
    }

    /// <summary>
    /// The qwen costume deliberately does not reshape results — its upstream strings were
    /// not researched, and guessing would be worse than saying so. Pinned so that stays a
    /// decision rather than becoming an oversight.
    /// </summary>
    [Fact]
    public async Task QwenCode_KeepsNbsResultStrings_AndSaysSo()
    {
        var harness = Harness<QwenCodeHarness>();

        var outcome = await harness.InvokeAsync("run_shell_command", "c15",
            new Dictionary<string, object?> { ["command"] = "echo hello" });

        Assert.Equal("hello\n\n[exit code: 0]", Text(outcome));
        Assert.Contains(harness.Omissions, o => o.StartsWith("result formatting:"));
    }

    // ---- harness ----

    private T Harness<T>() where T : NbHarness
    {
        NbHarness harness = new NbHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120), new ReadFileTool(_env),
            new WriteFileTool(_env), new EditFileTool(_env), new FindFilesTool(_env),
            new GrepTool(_env), new ListDirTool(_env), new FetchUrlTool(), searchWeb: null,
            applyPatch: new ApplyPatchTool(_env), applyPatchStyle: typeof(T) == typeof(NbHarness));

        if (typeof(T) != typeof(NbHarness))
            harness = HarnessRegistry.Create(NameOf<T>(), harness);

        harness.Configure(new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false), verbose: false);
        return (T)harness;
    }

    private static string NameOf<T>() =>
        typeof(T) == typeof(CodexHarness) ? CodexHarness.HarnessName
        : typeof(T) == typeof(ClaudeCodeHarness) ? ClaudeCodeHarness.HarnessName
        : QwenCodeHarness.HarnessName;

    private static string AddFilePatch(string name) =>
        $"*** Begin Patch\n*** Add File: {name}\n+hello\n*** End Patch";

    private static string Text(ToolOutcome? outcome)
    {
        Assert.NotNull(outcome);
        return outcome!.Value.Content.Result?.ToString() ?? "";
    }
}
