using nb.Shell;

namespace nb.Tests;

public class SearchWebToolTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("none")]
    [InlineData("None")]
    public void FromConfig_NoProvider_IsDeclaredOnly(string? provider)
    {
        var tool = SearchWebTool.FromConfig(provider, null);
        Assert.False(tool.HasBackend);
    }

    [Fact]
    public void FromConfig_BraveWithKey_HasBackend()
    {
        Assert.True(SearchWebTool.FromConfig("brave", "test-key").HasBackend);
    }

    // A silent downgrade to declared-only would make a run look like intent-capture
    // when its author believed it was live — the transcript would then mean something
    // other than what its reader thinks. Fail loudly instead.
    [Fact]
    public void FromConfig_BraveWithoutKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => SearchWebTool.FromConfig("brave", "  "));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void FromConfig_UnknownProvider_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => SearchWebTool.FromConfig("bing", "k"));
        Assert.Contains("none, brave", ex.Message);
    }

    // The load-bearing invariant: an unconfigured backend is a SUCCESSFUL call.
    // Returning it as an error would feed ExitReasons.ToolErrorLimit and abort exactly
    // the runs where the model keeps trying to search — destroying the measurement in
    // the case the tool exists to capture. See plans/web-search.md.
    [Fact]
    public async Task DeclaredOnly_ReturnsSuccessNotError()
    {
        var result = await SearchWebTool.FromConfig("none", null).SearchAsync("anything");

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Equal(SearchWebTool.DeclaredOnlyNote, result.Output);
    }

    // Terse by design: it states the configuration fact and stops. Instructing the model
    // not to invent results would make nb steer the behavior it exists to observe.
    [Fact]
    public void DeclaredOnlyNote_StatesConfigStateWithoutInstructing()
    {
        var note = SearchWebTool.DeclaredOnlyNote;

        Assert.Contains("No search backend is configured", note);
        Assert.Contains("not a search failure", note);
        Assert.DoesNotContain("do not", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invent", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EmptyQuery_IsAnError()
    {
        var result = await SearchWebTool.FromConfig("none", null).SearchAsync("   ");
        Assert.False(result.Success);
    }

    [Fact]
    public void FromConfig_DefaultsToBravesOwnEndpointAndHeader()
    {
        var tool = SearchWebTool.FromConfig("brave", "k");

        Assert.Equal("https://api.search.brave.com", tool.Endpoint);
        Assert.Equal("X-Subscription-Token", tool.AuthHeader);
    }

    // A gateway fronting Brave holds the real subscription token and authenticates nb
    // its own way — same dialect, different host and header.
    [Fact]
    public void FromConfig_HonorsEndpointAndAuthHeader()
    {
        var tool = SearchWebTool.FromConfig("brave", "Bearer tok", "http://router.local:8090/x/brave/", "Authorization");

        Assert.Equal("http://router.local:8090/x/brave", tool.Endpoint);   // trailing slash trimmed
        Assert.Equal("Authorization", tool.AuthHeader);
        Assert.True(tool.HasBackend);
    }

    [Fact]
    public void FromConfig_NonAbsoluteEndpoint_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => SearchWebTool.FromConfig("brave", "k", "router.local:8090"));
        Assert.Contains("Search.Endpoint", ex.Message);
    }

    [Fact]
    public void CreateTool_IsNamedSearchWeb()
    {
        // Not `web_search`: that collides with a name providers bind to a built-in
        // schema, and it breaks nb's verb-first convention (plans/web-search.md).
        Assert.Equal("search_web", SearchWebTool.FromConfig("none", null).CreateTool().Name);
    }
}

public class ApprovalPolicySearchTests
{
    private static ApprovalPolicy Policy(ApprovalDefault @default = ApprovalDefault.Prompt) =>
        new(trust: false, new ApprovalPatterns(), _ => false, null, @default);

    [Fact]
    public void Search_DeniedByDefault()
    {
        Assert.Equal(ApprovalDecision.Deny, Policy().DecideSearch());
    }

    [Fact]
    public void Search_AllowedWhenGranted()
    {
        var p = Policy();
        p.SetSearchAllowed(true);
        Assert.Equal(ApprovalDecision.Allow, p.DecideSearch());
    }

    // nb never prompts, so without a grant every run reads as a denial — the grant is
    // what makes live search usable at all.
    [Fact]
    public void Search_DeniedUnderDefaultDeny()
    {
        Assert.Equal(ApprovalDecision.Deny, Policy(ApprovalDefault.Deny).DecideSearch());
    }

    [Fact]
    public void Search_GrantWinsOverDefaultDeny()
    {
        var p = Policy(ApprovalDefault.Deny);
        p.SetSearchAllowed(true);
        Assert.Equal(ApprovalDecision.Allow, p.DecideSearch());
    }

    [Fact]
    public void Search_TrustAloneDoesNotGrant()
    {
        // Trust is a working-directory sandbox concept; search leaves the machine.
        var p = new ApprovalPolicy(trust: true, new ApprovalPatterns(), _ => false);
        Assert.Equal(ApprovalDecision.Deny, p.DecideSearch());
    }
}
