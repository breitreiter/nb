using nb.Shell;

namespace nb.Tests;

/// <summary>
/// The unsandboxed bash tool passes the command to bash verbatim (one argv entry),
/// so bash's own quoting rules decide what expands. These tests pin that contract —
/// both the corruption they fix (bugs/Bash_Escapes_Dollar_Inside_Single_Quotes.md)
/// and the expansion that is now deliberately live.
/// </summary>
public class BashQuotingTests
{
    private static BashTool Tool() => new(ShellEnvironment.Detect(Array.Empty<string>()));

    private static async Task<string> Run(string command)
    {
        var result = await Tool().ExecuteAsync(command, cwd: Path.GetTempPath());
        return result.Stdout.Trim();
    }

    // --- No backslash may reach a second interpreter (the reported bug) ---

    [Fact]
    public async Task SingleQuotedDollar_ReachesInnerInterpreterIntact()
    {
        // The `ruby -e 'puts $LOAD_PATH'` shape, without requiring ruby installed.
        Assert.Equal("puts $LOAD_PATH", await Run("echo 'puts $LOAD_PATH'"));
    }

    [Fact]
    public async Task AwkFieldReference_SelectsTheField()
    {
        Assert.Equal("def", await Run("echo abc def | awk '{print $2}'"));
    }

    [Fact]
    public async Task AnchoredGrep_Matches()
    {
        // The quiet one: this used to exit 1 with no output — a wrong answer with
        // nothing in the transcript to explain it.
        Assert.Equal("foo", await Run("printf 'foo\\nfoobar\\n' | grep 'foo$'"));
    }

    [Fact]
    public async Task SedAnchoredSubstitution_Applies()
    {
        Assert.Equal("foo", await Run("echo foobar | sed 's/bar$//'"));
    }

    [Fact]
    public async Task DoubleQuotesInCommand_SurviveIntact()
    {
        Assert.Equal("a \"b\" c", await Run("echo 'a \"b\" c'"));
    }

    [Fact]
    public async Task LiteralBackslash_IsNotDoubled()
    {
        Assert.Equal("a\\b", await Run("printf '%s' 'a\\b'"));
    }

    // --- Deliberate behaviour change: real bash expansion is now live ---
    //
    // Previously nothing ever expanded, because every $ and backtick was escaped.
    // These pin the new semantics rather than letting them flip silently. Note this
    // also makes bugs/shell-tool-no-filesystem-sandbox.md Hole #2 reachable
    // unsandboxed — command substitution behind an auto-approved prefix now runs.
    // That hole is real and tracked there; it was only ever hidden by this quoting
    // accident, not closed by it.

    [Fact]
    public async Task DoubleQuotedVariable_Expands()
    {
        Assert.Equal(Environment.GetEnvironmentVariable("HOME"), await Run("echo \"$HOME\""));
    }

    [Fact]
    public async Task CommandSubstitution_Runs()
    {
        Assert.Equal("INNER", await Run("echo $(echo INNER)"));
    }

    [Fact]
    public async Task BacktickSubstitution_Runs()
    {
        Assert.Equal("INNER", await Run("echo `echo INNER`"));
    }

    [Fact]
    public async Task SingleQuotedVariable_DoesNotExpand()
    {
        // The other half of the contract: bash's rules, not "everything expands".
        Assert.Equal("$HOME", await Run("echo '$HOME'"));
    }
}
