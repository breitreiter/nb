using nb.Shell;

namespace nb.Tests;

public class ApprovalPolicyTests
{
    private static ApprovalPolicy Policy(bool trust = false, IEnumerable<string>? approve = null, Func<string, bool>? mcp = null) =>
        new(trust, approve is null ? new ApprovalPatterns() : new ApprovalPatterns(approve), mcp ?? (_ => false));

    private static (ApprovalDecision, string?) Bash(ApprovalPolicy p, string command) =>
        p.DecideBash(command, CommandClassifier.Classify(command), cwd: "/tmp/work", bashPresent: true);

    [Fact]
    public void Bash_ApprovePatternWins()
    {
        // cat isn't in the safe allowlist, so only --approve can clear it.
        var (d, reason) = Bash(Policy(approve: new[] { "cat *" }), "cat notes.txt");
        Assert.Equal(ApprovalDecision.Allow, d);
        Assert.Equal("pre-approved", reason);
    }

    [Fact]
    public void Bash_SafeAllowlistAutoApproves()
    {
        var (d, reason) = Bash(Policy(), "git status");
        Assert.Equal(ApprovalDecision.Allow, d);
        Assert.Equal("safe", reason);
    }

    [Fact]
    public void Bash_DangerousNeverSafe_FallsToPrompt()
    {
        // rm -rf is dangerous, not pre-approved, trust off → prompt.
        var (d, _) = Bash(Policy(), "rm -rf build");
        Assert.Equal(ApprovalDecision.Prompt, d);
    }

    [Fact]
    public void Bash_TrustClearsNonDangerousRun()
    {
        // touch is a non-dangerous Run command, not in the safe allowlist.
        var (dNoTrust, _) = Bash(Policy(trust: false), "touch out.txt");
        Assert.Equal(ApprovalDecision.Prompt, dNoTrust);

        var (dTrust, reason) = Bash(Policy(trust: true), "touch out.txt");
        Assert.Equal(ApprovalDecision.Allow, dTrust);
        Assert.Equal("trust", reason);
    }

    [Fact]
    public void Bash_UnknownReadCommand_Prompts()
    {
        // cat is a Read command but not safe-listed; without --approve/trust → prompt.
        var (d, _) = Bash(Policy(), "cat /etc/passwd");
        Assert.Equal(ApprovalDecision.Prompt, d);
    }

    [Fact]
    public void Bash_TrustDoesNotClearDangerous()
    {
        var (d, _) = Bash(Policy(trust: true), "sudo rm -rf /");
        Assert.Equal(ApprovalDecision.Prompt, d);
    }

    [Fact]
    public void Mcp_AlwaysAllowedAutoApproves_ElsePrompts()
    {
        var p = Policy(mcp: name => name == "tester_current_time");
        Assert.Equal(ApprovalDecision.Allow, p.DecideMcp("tester_current_time"));
        Assert.Equal(ApprovalDecision.Prompt, p.DecideMcp("tester_delete_all"));
    }

    [Fact]
    public void Mcp_Glob_MatchesCompositeName_SlashAliasesUnderscore()
    {
        var p = new ApprovalPolicy(false, new ApprovalPatterns(), _ => false, mcpGlobs: new[] { "tester/*" });
        Assert.Equal(ApprovalDecision.Allow, p.DecideMcp("tester_current_time"));
        Assert.Equal(ApprovalDecision.Prompt, p.DecideMcp("other_tool"));
    }

    [Fact]
    public void Path_InSandboxAllows_OutsidePrompts()
    {
        var p = Policy();
        Assert.Equal(ApprovalDecision.Allow, p.DecidePath(inSandbox: true));
        Assert.Equal(ApprovalDecision.Prompt, p.DecidePath(inSandbox: false));
    }

    [Fact]
    public void DefaultDeny_TurnsEveryNonMatchIntoDeny()
    {
        var p = new ApprovalPolicy(false, new ApprovalPatterns(), _ => false, @default: ApprovalDefault.Deny);
        Assert.Equal(ApprovalDecision.Deny, Bash(p, "cat /etc/passwd").Item1); // unmatched bash
        Assert.Equal(ApprovalDecision.Deny, p.DecidePath(inSandbox: false));   // out-of-sandbox path
        Assert.Equal(ApprovalDecision.Deny, p.DecideFetch());                  // fetch never auto
        Assert.Equal(ApprovalDecision.Deny, p.DecideMcp("unlisted_tool"));     // unlisted MCP
    }

    [Fact]
    public void DefaultDeny_StillAllowsMatches()
    {
        var p = new ApprovalPolicy(trust: false, new ApprovalPatterns(new[] { "cat *" }), _ => false, @default: ApprovalDefault.Deny);
        Assert.Equal(ApprovalDecision.Allow, Bash(p, "cat notes.txt").Item1);  // --approve match wins over deny
        Assert.Equal(ApprovalDecision.Allow, p.DecidePath(inSandbox: true));   // in-sandbox is a match
    }

    [Fact]
    public void Fetch_NeverAutoApproves()
    {
        Assert.Equal(ApprovalDecision.Prompt, Policy().DecideFetch());
    }

    [Fact]
    public void Sandbox_DefaultsToNone()
    {
        var p = Policy();
        Assert.Equal(SandboxMode.None, p.Sandbox);
        Assert.False(p.SandboxNet);
    }

    [Fact]
    public void SetSandbox_UpdatesModeAndNet()
    {
        // The `approval sandbox bwrap-net` directive layers onto the built policy.
        var p = Policy();
        p.SetSandbox(SandboxMode.Bwrap, allowNet: true);
        Assert.Equal(SandboxMode.Bwrap, p.Sandbox);
        Assert.True(p.SandboxNet);
    }
}
