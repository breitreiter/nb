using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// What a tool call *returns* is the observation half of the loop. These exact strings
/// were inline in the capability methods before the formatters were extracted, and
/// nothing else asserts them.
/// </summary>
public class ToolResultFormattingTests : IDisposable
{
    private readonly string _dir;
    private readonly ShellEnvironment _env;

    public ToolResultFormattingTests()
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

    [Fact]
    public async Task Nb_ShellResult_KeepsItsLabelledStreamsAndExitCodeFooter()
    {
        var outcome = await Harness().InvokeAsync("bash", "c1",
            new Dictionary<string, object?> { ["command"] = "echo hello" });

        Assert.Equal("hello\n\n[exit code: 0]", Text(outcome));
    }

    [Fact]
    public async Task Nb_EditAndWrite_KeepTheirSuccessfullyStrings()
    {
        var harness = Harness();
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
        var outcome = await Harness().InvokeAsync("apply_patch", "c4",
            new Dictionary<string, object?> { ["input"] = AddFilePatch("new.txt") });

        Assert.Equal("Applied patch: 1 added, 0 updated, 0 deleted", Text(outcome));
    }

    // ---- codex: verified against openai/codex ----
    // ---- harness ----

    private NbHarness Harness()
    {
        var harness = new NbHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120), new ReadFileTool(_env),
            new WriteFileTool(_env), new EditFileTool(_env), new FindFilesTool(_env),
            new GrepTool(_env), new ListDirTool(_env), new FetchUrlTool(), searchWeb: null,
            applyPatch: new ApplyPatchTool(_env), applyPatchStyle: true);

        harness.Configure(new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false), verbose: false);
        return harness;
    }

    private static string AddFilePatch(string name) =>
        $"*** Begin Patch\n*** Add File: {name}\n+hello\n*** End Patch";

    private static string Text(ToolOutcome? outcome)
    {
        Assert.NotNull(outcome);
        return outcome!.Value.Content.Result?.ToString() ?? "";
    }
}
