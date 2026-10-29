using System.Text.Json;
using Microsoft.Extensions.Configuration;
using nb;
using nb.Transcript;

namespace nb.Tests;

// Nb.JudgeAsync / `nb --judge`: the oracle's judgement on one message, outside a run. The
// Mock's scripted verdict rides on the judged message (MOCK:oracle=), as in the loop.
public class JudgeTests
{
    private const string Sheet = "## deploy-target\nStaging only.\n\n## customer-name\nAcme.\n";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ActiveProvider"] = "Mock",
        ["ChatProviders:0:Name"] = "Mock",
        ["ChatProviders:1:Name"] = "Judge",
        ["ChatProviders:1:Provider"] = "Mock",
    }).Build();

    private static Task<JudgeResult> Judge(string message, string? provider = null) =>
        Nb.JudgeAsync(Config(), Sheet, message, provider, NbFacadeTests.Options());

    [Fact]
    public async Task Hit_SelectsTheEntriesAndComposesTheAnswer()
    {
        var result = await Judge("Which environment, and for whom? MOCK:oracle=customer-name,deploy-target");

        Assert.Equal(OracleVerdictKind.Hit, result.Verdict);
        Assert.Equal(["customer-name", "deploy-target"], result.Keys);
        Assert.Equal("Staging only.\n\nAcme.", result.Answer); // sheet order, verbatim
        Assert.Equal(15, result.Usage!.Total);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("MOCK:oracle=DONE", OracleVerdictKind.Done)]
    [InlineData("MOCK:oracle=MISS", OracleVerdictKind.Miss)]
    public async Task DoneAndMiss_CarryNoAnswer(string rider, OracleVerdictKind kind)
    {
        var result = await Judge($"A message. {rider}");

        Assert.Equal(kind, result.Verdict);
        Assert.Empty(result.Keys);
        Assert.Null(result.Answer);
    }

    [Fact]
    public async Task AnUnknownId_IsDoneWithAWarning()
    {
        var result = await Judge("A message. MOCK:oracle=bogus");

        Assert.Equal(OracleVerdictKind.Done, result.Verdict);
        Assert.Equal("bogus", result.Raw);
        Assert.Contains(result.Warnings, w => w.Contains("not on the sheet (bogus)"));
    }

    // The loop does not judge a turn with no text; neither does the judge, and no call is made.
    [Fact]
    public async Task AnEmptyMessage_IsDoneWithoutACall()
    {
        var result = await Judge("  \n");

        Assert.Equal(OracleVerdictKind.Done, result.Verdict);
        Assert.Null(result.Usage);
        Assert.Contains(result.Warnings, w => w.Contains("nothing to judge"));
    }

    [Fact]
    public async Task ANamedProvider_Judges_AndAnUnknownOneIsAStartupError()
    {
        Assert.Equal(OracleVerdictKind.Hit, (await Judge("MOCK:oracle=deploy-target", "Judge")).Verdict);
        await Assert.ThrowsAsync<NbStartupException>(() => Judge("MOCK:oracle=deploy-target", "Nope"));
    }

    // The two oracle paths proctor's gate compares: what the in-process loop appends as the
    // user's turn is what the judge answers for the same message.
    [Fact]
    public async Task TheAnswer_IsTheTurnTheInProcessLoopAppends()
    {
        const string ask = "Which environment should I deploy to? MOCK:oracle=deploy-target";
        var run = await Nb.Program().Oracle(Sheet).Run($"MOCK:response={ask}").RunAsync(Config(), NbFacadeTests.Options());
        var appended = run.Events.OfType<UserEvent>().Single(u => u.Source == "oracle");

        var judged = await Judge(ask);

        Assert.Equal(appended.Text, judged.Answer);
        Assert.Equal(appended.Keys, judged.Keys);
    }

    [Fact]
    public void ToJson_IsOneLine_AndOmitsWhatIsAbsent()
    {
        var hit = new JudgeResult { Verdict = OracleVerdictKind.Hit, Keys = ["port"], Answer = "8080 — the default", Raw = "port", Usage = new UsageInfo { Input = 1, Output = 2, Total = 3 } };
        var done = new JudgeResult { Verdict = OracleVerdictKind.Done, Raw = "DONE", Warnings = ["w"] };

        Assert.Equal("""{"verdict":"hit","keys":["port"],"answer":"8080 — the default","raw":"port","usage":{"input":1,"output":2,"total":3},"warnings":[]}""", hit.ToJson());
        using var doc = JsonDocument.Parse(done.ToJson());
        Assert.False(doc.RootElement.TryGetProperty("answer", out _));
        Assert.False(doc.RootElement.TryGetProperty("usage", out _));
        Assert.Equal("done", doc.RootElement.GetProperty("verdict").GetString());
    }
}
