using System.Runtime.CompilerServices;
using System.Text;
using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// Golden master over the <b>refusal nb gives the model</b> — the denial companion to
/// <see cref="ToolSurfaceGoldenTests"/> (plans/approval-without-prompts.md step 5).
///
/// Denial strings are model-visible text a refactor can silently change, and since
/// approval stopped being a keypress the refusal is the <i>only</i> thing a blocked model
/// reads. Captured at <see cref="NbHarness.InvokeAsync"/>, the boundary that produces it;
/// the error tracker's retry nudge downstream is not pinned here. Both halves are
/// recorded: the model-facing result string and the human-facing stderr.
///
/// To re-baseline after an intentional change: <c>UPDATE_GOLDEN=1 dotnet build &amp;&amp;
/// dotnet test --no-build</c>, then read the diff before committing it.
/// </summary>
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

    /// <summary>A call to refuse.</summary>
    private sealed record Case(string Label, string Tool, Dictionary<string, object?> Args);

    // A shell command, a write outside the sandbox, a network fetch, a read outside the sandbox.
    private static readonly Case[] NbCases =
    [
        new("shell", "bash", new() { ["command"] = "cat /etc/passwd", ["description"] = "read the password file" }),
        new("write-outside-cwd", "write_file", new() { ["path"] = "/etc/motd", ["content"] = "hello\n" }),
        new("network-fetch", "fetch_url", new() { ["url"] = "https://example.com/data.json" }),
        new("read-outside-cwd", "read_file", new() { ["path"] = "/etc/hostname" }),
    ];

    [Fact]
    public Task Denials_Nb() => AssertGolden("nb", NbCases);

    // ---- harness ----

    private async Task AssertGolden(string name, Case[] cases)
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
                var (model, stderr, verdict, reason) = await Refuse(tier, c);

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
        ApprovalDefault tier, Case c)
    {
        var policy = new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false, null, tier);
        var ledger = new ApprovalLedger();

        var instance = new NbHarness(
            new BashTool(_env, defaultTimeoutSeconds: 120),
            new ReadFileTool(_env), new WriteFileTool(_env), new EditFileTool(_env),
            new FindFilesTool(_env), new GrepTool(_env), new ListDirTool(_env),
            new FetchUrlTool(), searchWeb: null, new ApplyPatchTool(_env), applyPatchStyle: false);
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
