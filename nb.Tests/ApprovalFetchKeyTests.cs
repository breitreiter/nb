using nb.Shell;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The `approval fetch` key. It exists because removing the interactive prompt removed
/// fetch_url's only execution path: <see cref="ApprovalPolicy.DecideFetch"/> matched
/// nothing, so a keypress was the sole way the call ever ran. With prompts gone the tool
/// would have been advertised but unreachable in every configuration.
///
/// Deliberately a separate key from `search`: reaching an arbitrary URL and running a web
/// search are different grants.
/// </summary>
public class ApprovalFetchKeyTests
{
    private static ApprovalPolicy NewPolicy() =>
        new(trust: false, new ApprovalPatterns(Array.Empty<string>()), _ => false);

    [Fact]
    public void Fetch_IsDeniedByDefault()
    {
        var policy = NewPolicy();

        Assert.NotEqual(ApprovalDecision.Allow, policy.DecideFetch());
    }

    [Fact]
    public void Fetch_AllowsOnceGranted()
    {
        var policy = NewPolicy();
        policy.SetFetchAllowed(true);

        Assert.Equal(ApprovalDecision.Allow, policy.DecideFetch());
    }

    [Fact]
    public void SearchGrant_DoesNotConferFetch()
    {
        var policy = NewPolicy();
        policy.SetSearchAllowed(true);

        Assert.Equal(ApprovalDecision.Allow, policy.DecideSearch());
        Assert.NotEqual(ApprovalDecision.Allow, policy.DecideFetch());
    }

    [Fact]
    public void FetchGrant_DoesNotConferSearch()
    {
        var policy = NewPolicy();
        policy.SetFetchAllowed(true);

        Assert.Equal(ApprovalDecision.Allow, policy.DecideFetch());
        Assert.NotEqual(ApprovalDecision.Allow, policy.DecideSearch());
    }

    [Fact]
    public void Parser_AcceptsTheFetchDirective()
    {
        var program = ProgramParser.Parse("approval fetch allow\nrun go");

        var approval = Assert.Single(program.OfType<ApprovalEvent>());
        Assert.Equal("fetch", approval.Key);
        Assert.Equal("allow", approval.Value);
    }
}
