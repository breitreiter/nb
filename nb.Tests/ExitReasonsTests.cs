using nb.Transcript;

namespace nb.Tests;

public class ExitReasonsTests
{
    [Theory]
    [InlineData(ExitReasons.Ok, 0)]
    [InlineData(ExitReasons.ProviderError, 2)]
    [InlineData(ExitReasons.RateLimited, 3)]
    [InlineData(ExitReasons.MaxToolCalls, 3)]
    [InlineData(ExitReasons.ToolErrorLimit, 3)]
    [InlineData(ExitReasons.TokenBudget, 3)]
    [InlineData(ExitReasons.TimeBudget, 3)]
    [InlineData(ExitReasons.ApprovalDenied, 4)]
    public void KnownReasons_MapToTheirDocumentedCode(string reason, int code)
        => Assert.Equal(code, ExitReasons.ToExitCode(reason));

    // oracle_budget is a limit, like the other budgets.
    [Fact]
    public void OracleBudget_IsALimit()
        => Assert.Equal(3, ExitReasons.ToExitCode(ExitReasons.OracleBudget));

    // oracle_miss exits 0 BY DECISION: the run ended the way it would have without an
    // oracle, and only the label differs. The `_ => 0` fallback would produce the same
    // number, which is exactly why this is asserted separately — the pair below is the
    // property that matters, and it is the one a future unlisted reason would break.
    [Fact]
    public void OracleMiss_ExitsZero_ByDecisionNotByFallback()
    {
        Assert.Equal(0, ExitReasons.ToExitCode(ExitReasons.OracleMiss));
        Assert.Equal(0, ExitReasons.ToExitCode("a reason nobody has defined"));
    }
}
