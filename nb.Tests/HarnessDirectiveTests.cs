using nb.Harness;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// The <c>harness</c> directive: which harness a run wears. Only nb's own surface is
/// registered today — costumes land later (plans/harness-emulation.md) — so these pin
/// the directive's shape, its round-trip, and the refusal to accept a name nobody
/// implements.
/// </summary>
public class HarnessDirectiveTests
{
    [Fact]
    public void Harness_ParsesToEvent()
    {
        var events = ProgramParser.Parse("harness nb\nrun hello\n");

        var harness = Assert.IsType<HarnessEvent>(events[0]);
        Assert.Equal("nb", harness.Name);
        Assert.Null(harness.Turn); // a config directive, not a conversation turn
    }

    [Fact]
    public void Harness_CanonicalizesCasing()
    {
        var events = ProgramParser.Parse("harness NB\n");

        Assert.Equal("nb", Assert.IsType<HarnessEvent>(events[0]).Name);
    }

    /// <summary>
    /// A hard error, not a warning. Falling back to nb's surface while the program says
    /// it is wearing another agent's would silently invalidate every comparison drawn
    /// from the run.
    /// </summary>
    [Fact]
    public void Harness_UnknownName_IsRejectedWithLineAndKnownNames()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("# comment\nharness not-a-harness\n"));

        Assert.Contains("line 2", ex.Message);
        Assert.Contains("not-a-harness", ex.Message);
        Assert.Contains("nb", ex.Message);
    }

    [Fact]
    public void Harness_RequiresAValue()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("harness\n"));
        Assert.Contains("requires a value", ex.Message);
    }

    [Fact]
    public void Harness_IsListedAmongKnownDirectives()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("harnes nb\n"));
        Assert.Contains("harness", ex.Message);
    }

    [Fact]
    public void HarnessEvent_RoundTripsThroughJsonl()
    {
        var line = TranscriptSerializer.SerializeEvent(new HarnessEvent { Name = "nb" });
        Assert.Contains("\"type\":\"harness\"", line);

        var warnings = new List<string>();
        var parsed = TranscriptSerializer.Parse(line, warnings);

        Assert.Empty(warnings);
        Assert.Equal("nb", Assert.IsType<HarnessEvent>(parsed[0]).Name);
    }

    [Fact]
    public void Evaluator_DefaultsToNbsOwnHarness()
    {
        Assert.Equal("nb", HarnessRegistry.Default);
        Assert.True(HarnessRegistry.IsKnown("nb"));

        // A bogus name rather than a not-yet-built costume: the last two placeholders
        // here both became real, and each broke this test on the day it shipped.
        Assert.False(HarnessRegistry.IsKnown("not-a-harness"));
    }

    /// <summary>
    /// The trailer carries the harness only when it is not the default, so every
    /// existing transcript stays byte-identical — the same convention the estimated-usage
    /// flag follows.
    /// </summary>
    [Fact]
    public void Trailer_OmitsTheDefaultHarness()
    {
        var withDefault = TranscriptSerializer.SerializeEvent(new ResultEvent { ExitReason = "ok" });
        Assert.DoesNotContain("harness", withDefault);

        var withCostume = TranscriptSerializer.SerializeEvent(new ResultEvent { ExitReason = "ok", Harness = "codex" });
        Assert.Contains("\"harness\":\"codex\"", withCostume);
    }

    [Fact]
    public void Trailer_HarnessRoundTrips()
    {
        var line = TranscriptSerializer.SerializeEvent(new ResultEvent { ExitReason = "ok", Harness = "codex" });
        var parsed = TranscriptSerializer.Parse(line, new List<string>());

        Assert.Equal("codex", Assert.IsType<ResultEvent>(parsed[0]).Harness);
    }
}
