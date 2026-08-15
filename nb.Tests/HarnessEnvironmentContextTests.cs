using nb.Harness;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// The environment block — the second half of context furniture, alongside project
/// instructions (plans/harness-emulation.md, "What a harness owns", item 4).
///
/// Every real harness tells the model where it is before the model asks. A costume that
/// omits it produces a model that opens by running <c>pwd</c>, which is a behavioural diff
/// with an obvious cause that would otherwise get charged to the prompt.
/// </summary>
public class HarnessEnvironmentContextTests : IDisposable
{
    private readonly string _dir;
    private readonly ShellEnvironment _env;

    public HarnessEnvironmentContextTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-env-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _env = ShellEnvironment.Detect();
        _env.SetCwd(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>
    /// §5.5 is a promise, not a default: a program that names no harness gets exactly the
    /// system text it writes. Adding a channel to the costumes must not quietly add one
    /// to nb's own surface.
    /// </summary>
    [Fact]
    public void NbHarness_SendsNoEnvironmentBlock()
    {
        Assert.Null(new NbHarness().EnvironmentContext());
        Assert.Empty(new NbHarness().LeadingContext());
    }

    /// <summary>
    /// Codex's verified layout: one available environment renders flat — cwd and shell
    /// inline rather than wrapped in an &lt;environments&gt; list — which is nb's case
    /// exactly. Element order is the render function's own.
    /// </summary>
    [Fact]
    public void Codex_RendersTheFlatSingleEnvironmentLayout()
    {
        var text = Harness<CodexHarness>().EnvironmentContext()!;

        Assert.StartsWith("<environment_context>\n", text);
        Assert.EndsWith("\n</environment_context>", text);
        Assert.Contains($"  <cwd>{_dir}</cwd>", text);
        Assert.Contains($"  <current_date>{DateTime.Now:yyyy-MM-dd}</current_date>", text);
        Assert.Contains("  <timezone>", text);

        // The wrapped form is for multiple environments; nb only ever has one.
        Assert.DoesNotContain("<environments>", text);

        Assert.Matches(@"<cwd>[\s\S]*<current_date>[\s\S]*<timezone>", text);
    }

    /// <summary>
    /// Codex's block also carries network and filesystem elements. Reproducing them would
    /// mean inventing spellings for its permission-profile enums, so they are dropped and
    /// declared instead.
    /// </summary>
    [Fact]
    public void Codex_DeclaresTheElementsItDoesNotEmit()
    {
        var harness = Harness<CodexHarness>();

        Assert.DoesNotContain("<filesystem>", harness.EnvironmentContext());
        var declared = Assert.Single(harness.Omissions, o => o.StartsWith("environment context:"));
        Assert.Contains("<filesystem>", declared);
    }

    [Fact]
    public void ClaudeCode_ReportsWhereItIsAndWhetherItIsAGitRepo()
    {
        var text = Harness<ClaudeCodeHarness>().EnvironmentContext()!;

        Assert.Contains("<env>", text);
        Assert.Contains($"Working directory: {_dir}", text);
        Assert.Contains("Is directory a git repo: No", text);
        Assert.Contains($"Today's date: {DateTime.Now:yyyy-MM-dd}", text);
    }

    [Fact]
    public void ClaudeCode_DetectsAGitRepositoryAboveTheWorkingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".git"));
        var nested = Directory.CreateDirectory(Path.Combine(_dir, "src", "deep")).FullName;
        _env.SetCwd(nested);

        Assert.Contains("Is directory a git repo: Yes", Harness<ClaudeCodeHarness>().EnvironmentContext());
    }

    /// <summary>
    /// The two costumes order their furniture oppositely, and both are right: Codex sends
    /// its environment after the workspace instructions, Claude Code carries environment
    /// in the system prompt and attaches CLAUDE.md last. That disagreement is the reason
    /// ordering belongs to the costume rather than to the evaluator.
    /// </summary>
    [Fact]
    public async Task TheTwoCostumesOrderTheirFurnitureOppositely()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "AGENTS.md"), "agents doc");
        await File.WriteAllTextAsync(Path.Combine(_dir, "CLAUDE.md"), "claude doc");

        var codex = Harness<CodexHarness>().LeadingContext();
        Assert.Contains("<INSTRUCTIONS>", codex[1]);
        Assert.StartsWith("<environment_context>", codex[2]);

        var claude = Harness<ClaudeCodeHarness>().LeadingContext();
        Assert.Contains("<env>", claude[1]);
        Assert.Contains("<system-reminder>", claude[2]);
    }

    /// <summary>
    /// qwen-code is the untouched tier, matching how it was left for result formatting:
    /// its upstream block was not researched, and an invented one is worse than none
    /// because the model reads it as fact.
    /// </summary>
    [Fact]
    public void QwenCode_SendsNoEnvironmentBlock_AndSaysSo()
    {
        var harness = Harness<QwenCodeHarness>();

        Assert.Null(harness.EnvironmentContext());
        Assert.Contains(harness.Omissions, o => o.StartsWith("environment block:"));
    }

    // ---- harness ----

    private T Harness<T>() where T : NbHarness
    {
        NbHarness harness = new NbHarness(new BashTool(_env, defaultTimeoutSeconds: 120), new ReadFileTool(_env));
        if (typeof(T) != typeof(NbHarness))
            harness = HarnessRegistry.Create(NameOf<T>(), harness);
        return (T)harness;
    }

    private static string NameOf<T>() =>
        typeof(T) == typeof(CodexHarness) ? CodexHarness.HarnessName
        : typeof(T) == typeof(ClaudeCodeHarness) ? ClaudeCodeHarness.HarnessName
        : QwenCodeHarness.HarnessName;
}
