using nb;
using nb.Transcript;

namespace nb.Tests;

public class OracleResolverTests
{
    private static readonly AnswerSheet Sheet = AnswerSheet.Parse("## deploy-target\nStaging.\n\n## customer-name\nAcme.\n");

    [Theory]
    [InlineData("DONE")]
    [InlineData("done")]
    [InlineData("```\nDONE\n```")]
    [InlineData("  DONE.\n\nbecause it is just reporting")]
    public void ParseVerdict_Done(string reply)
    {
        var warnings = new List<string>();
        Assert.Equal(OracleVerdictKind.Done, OracleResolver.ParseVerdict(reply, Sheet, warnings).Kind);
        Assert.Empty(warnings);
    }

    [Fact]
    public void ParseVerdict_Miss()
        => Assert.Equal(OracleVerdictKind.Miss, OracleResolver.ParseVerdict("MISS", Sheet, new List<string>()).Kind);

    [Theory]
    [InlineData("deploy-target", new[] { "deploy-target" })]
    [InlineData("deploy-target, customer-name", new[] { "deploy-target", "customer-name" })]
    [InlineData("`customer-name`", new[] { "customer-name" })]
    [InlineData("Deploy-Target", new[] { "deploy-target" })]
    public void ParseVerdict_Hit_SelectsCanonicalIds(string reply, string[] expected)
    {
        var v = OracleResolver.ParseVerdict(reply, Sheet, new List<string>());
        Assert.Equal(OracleVerdictKind.Hit, v.Kind);
        Assert.Equal(expected, v.Keys);
    }

    [Fact]
    public void ParseVerdict_UnknownIdsAreDropped_AndWarned()
    {
        var warnings = new List<string>();
        var v = OracleResolver.ParseVerdict("deploy-target, made-up", Sheet, warnings);
        Assert.Equal(new[] { "deploy-target" }, v.Keys);
        Assert.Contains(warnings, w => w.Contains("made-up"));
    }

    [Fact]
    public void ParseVerdict_Unreadable_IsDone_TheConservativeRule()
    {
        var warnings = new List<string>();
        var v = OracleResolver.ParseVerdict("I think the assistant wants to know the environment.", Sheet, warnings);
        Assert.Equal(OracleVerdictKind.Done, v.Kind);
        Assert.Contains(warnings, w => w.Contains("unreadable verdict"));
    }

    [Fact]
    public void ParseVerdict_TruncatedBeforeAnyText_IsDone_AndNamesTheCause()
    {
        var warnings = new List<string>();
        var response = new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant, ""))
            { FinishReason = Microsoft.Extensions.AI.ChatFinishReason.Length };
        Assert.Equal(OracleVerdictKind.Done, OracleResolver.ParseVerdict(response, Sheet, warnings).Kind);
        Assert.Contains(warnings, w => w.Contains("cut off by the output cap"));
    }

    [Fact]
    public void BuildPrompt_IsOneUserMessage_SentinelFirst_SubjectLast()
    {
        var prompt = OracleResolver.BuildPrompt(Sheet, "Which environment?");
        var only = Assert.Single(prompt);
        Assert.StartsWith(OracleProtocol.Sentinel, only.Text);
        // The judged text follows the marker and nothing follows the judged text — the
        // contract the Mock's scoped rider scan depends on.
        Assert.EndsWith(OracleProtocol.SubjectMarker + "\nWhich environment?", only.Text);
        Assert.Contains("### deploy-target\nStaging.", only.Text);
    }
}
