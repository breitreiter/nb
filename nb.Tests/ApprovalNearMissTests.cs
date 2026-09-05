using nb.Shell;

namespace nb.Tests;

/// <summary>
/// bugs/Denials_Do_Not_Name_The_Near_Miss.md — a denial named the tier that refused and
/// never the rung that nearly matched, so `no-match` and `default-deny` were the same
/// sentence twice. The load-bearing distinction is **skipped** (a rung is switched off
/// elsewhere) versus **refused** (the rung was evaluated and said no): they want
/// different fixes, and the two sibling reports are one of each.
///
/// Every test here was confirmed failing before the reason channel existed.
/// </summary>
public class ApprovalNearMissTests
{
    private static ApprovalPolicy Policy(bool trust = false, IEnumerable<string>? approve = null) =>
        new(trust, approve is null ? new ApprovalPatterns() : new ApprovalPatterns(approve), _ => false);

    private static string? Miss(ApprovalPolicy p, string command, string cwd = "/tmp/work") =>
        p.DecideBash(command, CommandClassifier.Classify(command), cwd, bashPresent: true).Miss;

    /// <summary>An allowed call has nothing to explain.</summary>
    [Fact]
    public void Allow_CarriesNoMiss()
    {
        Assert.Null(Miss(Policy(approve: new[] { "cat *" }), "cat notes.txt"));
    }

    /// <summary>
    /// bugs/No_Match_Denial_Does_Not_Name_The_Trust_Rung.md — "nothing in the approval
    /// policy allows it" while the actual gate was `Trust: false` in a config file in
    /// another repo. The denial must say the rung was *skipped*, and name what switched
    /// it off.
    /// </summary>
    [Fact]
    public void NoMatch_WithTrustOff_SaysTheTrustRungWasSkipped()
    {
        var miss = Miss(Policy(trust: false), "cat /etc/passwd");

        Assert.NotNull(miss);
        Assert.Contains("Trust=false", miss);
        Assert.Contains("skipped", miss);
    }

    /// <summary>
    /// bugs/Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md — with Trust on, the rung is
    /// reached and *refuses*. That is a different fix from the case above, so the string
    /// has to distinguish them.
    /// </summary>
    [Fact]
    public void NoMatch_WithTrustOn_SaysTheTrustRungRefused()
    {
        var miss = Miss(Policy(trust: true), "cat /etc/passwd");

        Assert.NotNull(miss);
        Assert.Contains("refused", miss);
        Assert.DoesNotContain("skipped", miss);
    }

    /// <summary>The miss names every rung that was consulted, so one read answers "what now".</summary>
    [Fact]
    public void NoMatch_NamesThePatternAndSafeListRungs()
    {
        var miss = Miss(Policy(), "cat /etc/passwd");

        Assert.NotNull(miss);
        Assert.Contains("no approval bash pattern matched", miss);
        Assert.Contains("safe-command list", miss);
    }

    /// <summary>
    /// Under `default deny` the safe list and trust are suppressed by design. The denial
    /// should say that, rather than implying the command merely failed to qualify.
    /// </summary>
    [Fact]
    public void DefaultDeny_SaysTheLowerRungsAreSuppressed()
    {
        var p = Policy();
        p.SetDefault(ApprovalDefault.Deny);

        var miss = Miss(p, "git status");   // on the safe list, but deny suppresses it

        Assert.NotNull(miss);
        Assert.Contains("suppress", miss);
    }

    /// <summary>A dangerous command is refused by trust for a nameable reason.</summary>
    [Fact]
    public void NoMatch_DangerousCommand_SaysSo()
    {
        var miss = Miss(Policy(trust: true), "sudo rm -rf /");

        Assert.NotNull(miss);
        Assert.Contains("dangerous", miss);
    }
}
