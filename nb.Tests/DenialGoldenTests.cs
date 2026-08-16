using System.Runtime.CompilerServices;
using System.Text;
using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// Golden master over the <b>refusal a costume gives the model</b> — the denial companion
/// to <see cref="ToolSurfaceGoldenTests"/> (plans/approval-without-prompts.md step 5).
///
/// The surface goldens exist because tool names and schemas are model-visible text that a
/// refactor can silently change. Denial strings are the same kind of text: once approval
/// stopped being a keypress, the refusal became the *only* thing a blocked model reads,
/// and step 6's capture established that the three emulated harnesses genuinely disagree
/// about what a refusal implies — terminal (qwen-code), escalatable (codex), human-in-loop
/// (claude-code). That disagreement moves model behaviour, so it belongs under a golden.
///
/// **These are captured before step 6's per-costume overrides land.** Every costume
/// currently gives nb's own terminal refusal, so the four files are deliberately near
/// identical — that sameness is the baseline, and step 6's divergence should show up here
/// as a reviewable diff rather than arriving unobserved.
///
/// Captured at <see cref="NbHarness.InvokeAsync"/>, the boundary that produces the
/// refusal and the one a costume owns. <c>ConversationManager</c> appends a generic
/// retry-budget nudge downstream ("bash has failed 1 time(s)…"); that belongs to the error
/// tracker, not the costume, and pinning it in four files would couple these goldens to an
/// unrelated subsystem. Both halves are recorded — the model-facing result string *and*
/// the human-facing stderr, since giving the human an authorizing directive is half of
/// what this plan set out to add.
///
/// To re-baseline after an intentional change: <c>UPDATE_GOLDEN=1 dotnet build &amp;&amp;
/// dotnet test --no-build</c>, then read the diff before committing it.
/// </summary>
[Collection(ConsoleBoundCollection.Name)]
public class DenialGoldenTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShellEnvironment _env;

    public DenialGoldenTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"nb-test-denial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_testDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    /// <summary>A call to refuse, in the costume's own wire vocabulary.</summary>
    private sealed record Case(string Label, string Tool, Dictionary<string, object?> Args);

    // Each costume gets the same four *situations* — a shell command, a write outside the
    // sandbox, a network fetch, a read outside the sandbox — spelled in its own tool names
    // and argument names. Same situations across costumes is the point: it is what makes
    // the four files comparable when step 6 makes them diverge.

    private static readonly Case[] NbCases =
    [
        new("shell", "bash", new() { ["command"] = "cat /etc/passwd", ["description"] = "read the password file" }),
        new("write-outside-cwd", "write_file", new() { ["path"] = "/etc/motd", ["content"] = "hello\n" }),
        new("network-fetch", "fetch_url", new() { ["url"] = "https://example.com/data.json" }),
        new("read-outside-cwd", "read_file", new() { ["path"] = "/etc/hostname" }),
    ];

    private static readonly Case[] ClaudeCodeCases =
    [
        new("shell", "Bash", new() { ["command"] = "cat /etc/passwd", ["description"] = "read the password file" }),
        new("write-outside-cwd", "Write", new() { ["file_path"] = "/etc/motd", ["content"] = "hello\n" }),
        new("network-fetch", "WebFetch", new() { ["url"] = "https://example.com/data.json" }),
        new("read-outside-cwd", "Read", new() { ["file_path"] = "/etc/hostname" }),
    ];

    // Codex withholds the file and network tools entirely, so its only refusable surface
    // is the shell and apply_patch. Fewer cases is a fact about the costume, not a gap.
    private static readonly Case[] CodexCases =
    [
        new("shell", "shell_command", new() { ["command"] = "cat /etc/passwd" }),
        new("patch-outside-cwd", "apply_patch", new()
        {
            ["input"] = "*** Begin Patch\n*** Add File: /etc/motd\n+hello\n*** End Patch",
        }),
    ];

    private static readonly Case[] QwenCodeCases =
    [
        new("shell", "run_shell_command", new() { ["command"] = "cat /etc/passwd", ["description"] = "read the password file" }),
        new("write-outside-cwd", "write_file", new() { ["file_path"] = "/etc/motd", ["content"] = "hello\n" }),
        new("network-fetch", "web_fetch", new() { ["url"] = "https://example.com/data.json" }),
        new("read-outside-cwd", "read_file", new() { ["file_path"] = "/etc/hostname" }),
    ];

    [Fact]
    public Task Denials_Nb() => AssertGolden("nb", harness: null, NbCases);

    [Fact]
    public Task Denials_ClaudeCode() => AssertGolden("claude-code", ClaudeCodeHarness.HarnessName, ClaudeCodeCases);

    [Fact]
    public Task Denials_Codex() => AssertGolden("codex", CodexHarness.HarnessName, CodexCases);

    [Fact]
    public Task Denials_QwenCode() => AssertGolden("qwen-code", QwenCodeHarness.HarnessName, QwenCodeCases);

    /// <summary>
    /// The costumes must not converge. The goldens would catch it, but only as a diff a
    /// reviewer has to interpret — this states the invariant directly, because a refusal
    /// class is a *behavioural* claim (retry / stop and wait / route around) and a
    /// refactor that quietly restored one shared string would still look tidy.
    /// </summary>
    [Fact]
    public async Task EachCostume_RefusesInItsOwnClass()
    {
        var shell = new Case("shell", "bash", new() { ["command"] = "cat /etc/passwd" });

        var nb = (await Refuse(null, ApprovalDefault.Prompt, shell)).Model;
        var claude = (await Refuse(ClaudeCodeHarness.HarnessName, ApprovalDefault.Prompt,
            new Case("shell", "Bash", new() { ["command"] = "cat /etc/passwd" }))).Model;
        var codex = (await Refuse(CodexHarness.HarnessName, ApprovalDefault.Prompt,
            new Case("shell", "shell_command", new() { ["command"] = "cat /etc/passwd" }))).Model;
        var qwen = (await Refuse(QwenCodeHarness.HarnessName, ApprovalDefault.Prompt,
            new Case("shell", "run_shell_command", new() { ["command"] = "cat /etc/passwd" }))).Model;

        Assert.Equal(4, new[] { nb, claude, codex, qwen }.Distinct().Count());

        // The class marker each one is for, not merely "they differ".
        Assert.Contains("will not succeed on retry", nb);          // terminal
        Assert.Contains("wait for the user", claude);              // human-in-loop
        Assert.Contains("failed in sandbox", codex);               // escalatable
        Assert.Contains("permission was declined", qwen);          // terminal, qwen's words

        // Codex must not tell the model to send an argument its schema never declared —
        // the same defect as naming an approval directive that does not exist.
        Assert.DoesNotContain("with_escalated_permissions", codex);
    }

    /// <summary>
    /// A refusal naming a tool the costume never advertised invites a retry under the name
    /// the model does know. Found by these goldens on their first run
    /// (plans/approval-without-prompts.md step 5).
    /// </summary>
    [Fact]
    public async Task Refusal_NamesTheToolTheModelCalled_NotNbsCanonicalOne()
    {
        var claude = await Refuse(ClaudeCodeHarness.HarnessName, ApprovalDefault.Prompt,
            new Case("write", "Write", new() { ["file_path"] = "/etc/motd", ["content"] = "x" }));

        Assert.Contains("Write", claude.Model);
        Assert.DoesNotContain("write_file", claude.Model);
        // The operator's line is nb's own channel and follows the same rule.
        Assert.DoesNotContain("write_file", claude.Stderr);
    }

    // ---- harness ----

    private async Task AssertGolden(string name, string? harness, Case[] cases)
    {
        var sb = new StringBuilder();

        // Both denial reasons, for every case. `prompt` is the permissive tier (the whole
        // auto-approve ladder ran and matched nothing); `deny` is the program asking for
        // refusal outright. They must stay distinguishable — that is the one thing the
        // collapse of ApprovalDecision could plausibly have destroyed.
        foreach (var (tier, tierLabel) in new[] { (ApprovalDefault.Prompt, "ladder-miss"), (ApprovalDefault.Deny, "default-deny") })
        {
            foreach (var c in cases)
            {
                var (model, stderr, verdict, reason) = await Refuse(harness, tier, c);

                sb.AppendLine($"=== {tierLabel} / {c.Label} / {c.Tool}");
                sb.AppendLine("--- ledger");
                sb.AppendLine($"{verdict ?? "(unrecorded)"} / {reason ?? "(none)"}");
                sb.AppendLine("--- model");
                sb.AppendLine(Scrub(model));
                sb.AppendLine("--- human (stderr)");
                sb.AppendLine(Scrub(stderr).TrimEnd());
                sb.AppendLine();
            }
        }

        var rendered = sb.ToString();
        var path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, rendered);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing golden file {path}. Run with UPDATE_GOLDEN=1 to create it, then review the contents before committing.");

        Assert.Equal(Normalize(await File.ReadAllTextAsync(path)), Normalize(rendered));
    }

    private async Task<(string Model, string Stderr, string? Verdict, string? Reason)> Refuse(
        string? harness, ApprovalDefault tier, Case c)
    {
        var policy = new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false, null, tier);
        var ledger = new ApprovalLedger();

        NbHarness instance = new NbHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120),
            new ReadFileTool(_env), new WriteFileTool(_env), new EditFileTool(_env),
            new FindFilesTool(_env), new GrepTool(_env), new ListDirTool(_env),
            new FetchUrlTool(), searchWeb: null, new ApplyPatchTool(_env), applyPatchStyle: false);
        if (harness is not null)
            instance = HarnessRegistry.Create(harness, instance);
        instance.Configure(policy, verbose: false, ledger);

        // The human half goes to stderr, so capture it rather than letting it leak into
        // the test runner's output — and so the golden can assert on it.
        var stderr = new StringWriter();
        var previous = Console.Error;
        Console.SetError(stderr);
        ToolOutcome? outcome;
        try
        {
            outcome = await instance.InvokeAsync(c.Tool, "call-1", c.Args, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previous);
        }

        Assert.NotNull(outcome);
        Assert.True(outcome!.Value.IsError,
            $"{c.Tool} was not refused under {tier}. The golden only means something if the call was actually denied.");

        ledger.TryGet("call-1", out var verdict, out var reason);
        return (outcome.Value.Content.Result?.ToString() ?? "", stderr.ToString(), verdict, reason);
    }

    /// <summary>
    /// The temp cwd is per-run, and file tools interpolate it into resolved paths, so it
    /// would otherwise make the golden machine-dependent — the same reason
    /// <see cref="ToolSurfaceGoldenTests"/> scrubs.
    /// </summary>
    private string Scrub(string text) => text.Replace(_env.ShellCwd, "<CWD>");

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();

    private static string GoldenPath(string name, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "golden", $"denial.{name}.txt");
}
