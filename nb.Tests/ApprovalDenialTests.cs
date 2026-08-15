using Microsoft.Extensions.Configuration;
using nb;
using nb.Harness;
using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The coverage that never existed while approval was a keypress
/// (plans/approval-without-prompts.md step 4).
///
/// Nothing in <c>nb.Tests</c> ever touched the prompt loop or the <c>NonInteractive</c>
/// gate — the suite only ever exercised <see cref="ApprovalPolicy"/>'s *decisions*, which
/// is exactly why a decision that resolved through an unrecorded keypress could sit there
/// for as long as it did. These drive real runs and assert on what a caller and a model
/// actually receive.
/// </summary>
[Collection(ConsoleBoundCollection.Name)]
public class ApprovalDenialTests
{
    private static IConfiguration MockConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
        }).Build();

    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }

    private static NbProgramBuilder BashOnly() =>
        Nb.Program().Add(new[] { new ToolsEvent { Reset = true, Add = new[] { "bash" } } });

    private static string DenialText(RunResult r) =>
        r.Events.OfType<ToolResultEvent>().Single().Output;

    // ---- The gate is gone, structurally -------------------------------------------

    /// <summary>
    /// The plan's first test — "an unmatched call denies identically with stdin a pipe and
    /// a TTY" — cannot be written as a behavioural assertion any more, and that is the
    /// point: <c>Console.IsInputRedirected</c> is not injectable, so the only way to make
    /// the two cases provably identical was to delete the branch that read it.
    ///
    /// So this asserts the deletion instead. It is a regression guard, not a tautology:
    /// reintroducing a TTY check is a one-line change that no behavioural test in this
    /// file would catch when run under a pipe (which is how CI runs them).
    /// </summary>
    [Fact]
    public void NbHarness_ExposesNoInteractivityGate()
    {
        // Substring matching on "Tty" would flag Object.GetType ("ge-tty-pe"), so match
        // the names an interactivity gate would actually be given.
        string[] gates = ["NonInteractive", "Interactive", "IsInteractive", "HasTty", "IsTty"];

        var found = typeof(NbHarness)
            .GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .Intersect(gates, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Empty(found);
    }

    /// <summary>
    /// There is no third disposition. A decision a keypress could resolve is precisely
    /// what made the same program behave two ways, so the enum having exactly two members
    /// is the invariant, not an implementation detail.
    /// </summary>
    [Fact]
    public void ApprovalDecision_HasNoOutcomeThatNeedsAHuman()
    {
        Assert.Equal(
            new[] { "Allow", "Deny" },
            Enum.GetNames<ApprovalDecision>().OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // ---- The refusal names how to authorize it -------------------------------------

    [Fact]
    public async Task Denial_NamesTheDirectiveThatWouldAllowIt()
    {
        var result = await BashOnly()
            .Run("MOCK:tool=bash cat /etc/passwd")
            .RunAsync(MockConfig(), Options());

        var text = DenialText(result);
        Assert.Contains("approval bash cat *", text);
        // …and says retrying is pointless, so the model routes around instead of looping.
        Assert.Contains("will not succeed on retry", text);
    }

    /// <summary>
    /// The remedy is keyed to the command's own program, not a generic placeholder — a
    /// human is meant to paste the line, so it has to be the line they actually need.
    /// </summary>
    [Fact]
    public async Task Denial_KeysTheRemedyToTheCommandsProgram()
    {
        var result = await BashOnly()
            .Run("MOCK:tool=bash curl https://example.com")
            .RunAsync(MockConfig(), Options());

        Assert.Contains("approval bash curl *", DenialText(result));
    }

    /// <summary>
    /// Where no directive can grant the thing, the refusal says so rather than inventing
    /// one. A write outside the working directory is the case with no authorizing key at
    /// all: `approval path` does not exist and `"Trust": true` reaches only cwd + temp.
    /// Naming a fake grant is the defect step 2 existed to fix, so it is pinned here.
    /// </summary>
    [Fact]
    public async Task Denial_OutsideCwd_InventsNoDirective()
    {
        var result = await Nb.Program()
            .Add(new[] { new ToolsEvent { Reset = true, Add = new[] { "write_file" } } })
            .Run("MOCK:tool=write_file /etc/motd")
            .RunAsync(MockConfig(), Options());

        var text = DenialText(result);
        Assert.Contains("No approval directive grants paths outside the working directory", text);
        Assert.DoesNotContain("approval path", text);
    }

    // ---- The two denial reasons stay distinguishable --------------------------------

    /// <summary>
    /// `approval default deny` and a standard-ladder miss are different facts about a run:
    /// one is "the program asked for this", the other is "nobody thought about it". The
    /// distinction used to live in control flow (Deny vs Prompt); with the decision
    /// collapsed it survives only in the rung and the message, so both are asserted.
    /// </summary>
    [Fact]
    public async Task DefaultDenyAndLadderMiss_ProduceDistinguishableRefusals()
    {
        var ladderMiss = await BashOnly()
            .Run("MOCK:tool=bash cat /etc/passwd")
            .RunAsync(MockConfig(), Options());

        var defaultDeny = await BashOnly()
            .Add(new[] { new ApprovalEvent { Key = "default", Value = "deny" } })
            .Run("MOCK:tool=bash cat /etc/passwd")
            .RunAsync(MockConfig(), Options());

        Assert.Contains("nothing in the approval policy allows it", DenialText(ladderMiss));
        Assert.Contains("the approval policy default is deny", DenialText(defaultDeny));
        Assert.NotEqual(DenialText(ladderMiss), DenialText(defaultDeny));

        Assert.Equal(ApprovalLedger.NoMatch, Reason(ladderMiss));
        Assert.Equal(ApprovalLedger.DefaultDeny, Reason(defaultDeny));

        static string? Reason(RunResult r) =>
            r.Events.OfType<ToolCallEvent>().Single().ApprovalReason;
    }

    /// <summary>
    /// `default deny` outranks the built-in safe list. `git status` auto-approves under the
    /// permissive tier, so it is the command that proves the tier still means something
    /// after the decision collapsed to two values.
    /// </summary>
    [Fact]
    public async Task DefaultDeny_RefusesEvenASafeListedCommand()
    {
        var result = await BashOnly()
            .Add(new[] { new ApprovalEvent { Key = "default", Value = "deny" } })
            .Run("MOCK:tool=bash git status")
            .RunAsync(MockConfig(), Options());

        Assert.Equal(1, result.Denied);
        Assert.Equal(ApprovalLedger.Deny, result.Events.OfType<ToolCallEvent>().Single().Approved);
    }

    // ---- The exit code a caller branches on -----------------------------------------

    /// <summary>
    /// A denial the model recovered from is not a failed run. The denial is still visible
    /// in the trailer, which is what makes "recovered" expressible at all.
    /// </summary>
    [Fact]
    public async Task DeniedButRecovered_ExitsZeroWithTheDenialStillOnTheWire()
    {
        var result = await BashOnly()
            .Run("MOCK:tool=bash cat /etc/passwd")
            .RunAsync(MockConfig(), Options());

        Assert.Equal(ExitReasons.Ok, result.ExitReason);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Denied);
    }

    /// <summary>
    /// A run the policy blocked outright exits 4. This is the code both published specs
    /// promised and nothing produced — <c>ExitReasons.ApprovalDenied</c> was defined,
    /// mapped, and unreachable (bugs/Approval_Denied_Exit_Code_Is_Unreachable.md).
    /// </summary>
    [Fact]
    public async Task TerminalDenial_ExitsFourNotThree()
    {
        var result = await BashOnly()
            .LoopOff()
            .Run("MOCK:loop=bash cat /etc/passwd")
            .RunAsync(MockConfig(), Options());

        Assert.Equal(ExitReasons.ApprovalDenied, result.ExitReason);
        Assert.Equal(4, result.ExitCode);
    }

    /// <summary>
    /// The other half of the same promise: a tool that keeps genuinely failing is still
    /// <c>tool_error_limit</c>. If both outcomes collapsed to 4, the code would carry no
    /// information — "grant something" and "the task is failing" want different responses.
    /// </summary>
    [Fact]
    public async Task RepeatedGenuineFailure_StillExitsThree()
    {
        var result = await Nb.Program()
            .Add(new[] { new ToolsEvent { Reset = true, Add = new[] { "read_file" } } })
            .LoopOff()
            .Budget("tool_calls", 10)
            .Run("MOCK:loop=read_file ./nonexistent-xyz.txt")
            .RunAsync(MockConfig(), Options());

        Assert.Equal(ExitReasons.ToolErrorLimit, result.ExitReason);
        Assert.Equal(3, result.ExitCode);
    }
}
