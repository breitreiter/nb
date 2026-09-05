using nb.Shell;

namespace nb.Tests;

/// <summary>
/// bugs/Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md — `CommandClassifier` takes a
/// redirect target as the command's path, so any command carrying `2>/dev/null` was
/// classified as a *write to /dev/null* and refused by the trust rung for being outside
/// cwd. The `find` in the report was incidental; the redirect was the whole cause.
///
/// The two tests asserting a redirect no longer denies were confirmed failing first; the
/// controls passed before and after.
/// </summary>
public class TrustRedirectTests
{
    private const string Cwd = "/tmp/work";

    private static ApprovalPolicy Policy(bool trust = true) =>
        new(trust, new ApprovalPatterns(), _ => false);

    private static (ApprovalDecision Decision, string Reason) Bash(ApprovalPolicy p, string command) =>
        p.DecideBash(command, CommandClassifier.Classify(command), Cwd, bashPresent: true) is var d
            ? (d.Decision, d.Reason)
            : default;

    /// <summary>
    /// The report's own command, reduced: a bare find over a path inside cwd, with a
    /// stderr redirect. Denied before the fix with approval_reason "no-match".
    /// </summary>
    [Fact]
    public void TrustAllowsAReadWithAStderrRedirectToTheNullSink()
    {
        var (decision, reason) = Bash(Policy(), $"find {Cwd}/docs -name glossary.md 2>/dev/null");

        Assert.Equal(ApprovalDecision.Allow, decision);
        Assert.Equal("trust", reason);
    }

    /// <summary>Control: the same command without the redirect was always allowed.</summary>
    [Fact]
    public void TrustAllowsTheSameReadWithoutTheRedirect()
    {
        var (decision, _) = Bash(Policy(), $"find {Cwd}/docs -name glossary.md");

        Assert.Equal(ApprovalDecision.Allow, decision);
    }

    /// <summary>
    /// The null sink is trusted because it is a sink, not because redirects are exempt.
    /// A redirect to a real path outside cwd still refuses — that classification is
    /// arguably correct and this fix deliberately leaves it alone.
    /// </summary>
    [Fact]
    public void ARedirectToARealPathOutsideCwdStillRefuses()
    {
        var (decision, _) = Bash(Policy(), $"find {Cwd}/docs -name glossary.md 2>/etc/nb-log");

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    /// <summary>The null sink is not a trust bypass: a dangerous command stays denied.</summary>
    [Fact]
    public void TheNullSinkDoesNotLaunderADangerousCommand()
    {
        var (decision, _) = Bash(Policy(), "sudo rm -rf / 2>/dev/null");

        Assert.Equal(ApprovalDecision.Deny, decision);
    }

    /// <summary>And it is still gated on trust being on at all.</summary>
    [Fact]
    public void WithTrustOffTheNullSinkChangesNothing()
    {
        var (decision, _) = Bash(Policy(trust: false), $"find {Cwd}/docs -name glossary.md 2>/dev/null");

        Assert.Equal(ApprovalDecision.Deny, decision);
    }
}
