using nb.Transcript;

namespace nb.Tests;

public class ProgramParserTests
{
    // The worked example from conversation-program-evaluator.md, in source syntax.
    private const string WorkedExample =
        """
        provider anthropic
        model claude-sonnet-5
        system you are a terse assistant
        user fabricated turn 1
        assistant fabricated answer 1
        run the real prompt
        """;

    [Fact]
    public void WorkedExample_DesugarsToDirectiveStream()
    {
        var events = ProgramParser.Parse(WorkedExample);

        Assert.Equal(6, events.Count);
        Assert.Equal("anthropic", Assert.IsType<ProviderEvent>(events[0]).Name);
        Assert.Equal("claude-sonnet-5", Assert.IsType<ModelEvent>(events[1]).Name);
        Assert.Equal("you are a terse assistant", Assert.IsType<SystemEvent>(events[2]).Text);
        Assert.Equal("fabricated turn 1", Assert.IsType<UserEvent>(events[3]).Text);
        Assert.Equal("fabricated answer 1", Assert.IsType<AssistantTextEvent>(events[4]).Text);
        Assert.Equal("the real prompt", Assert.IsType<RunEvent>(events[5]).Prompt);
    }

    [Fact]
    public void FirstTokenIsAlwaysTheVerb_ContentMayRepeatIt()
    {
        var e = Assert.IsType<SystemEvent>(ProgramParser.Parse("system system design is hard")[0]);
        Assert.Equal("system design is hard", e.Text);
    }

    [Fact]
    public void BareRun_HasNullPrompt()
    {
        Assert.Null(Assert.IsType<RunEvent>(ProgramParser.Parse("run")[0]).Prompt);
    }

    [Fact]
    public void Mcp_ParsesAddRemoveTokens()
    {
        var e = Assert.IsType<McpEvent>(ProgramParser.Parse("mcp +figma -tester")[0]);
        Assert.Equal(new[] { "figma" }, e.Add);
        Assert.Equal(new[] { "tester" }, e.Remove);
        Assert.False(e.Reset);
    }

    [Fact]
    public void Tools_None_IsReset()
    {
        var e = Assert.IsType<ToolsEvent>(ProgramParser.Parse("tools none")[0]);
        Assert.True(e.Reset);
    }

    [Fact]
    public void Approval_KeyIsFirstToken_ValueIsTheRest()
    {
        var e = Assert.IsType<ApprovalEvent>(ProgramParser.Parse("approval bash git status")[0]);
        Assert.Equal("bash", e.Key);
        Assert.Equal("git status", e.Value);  // value keeps its spaces
    }

    [Fact]
    public void Approval_Default_LowercasesKey()
    {
        var e = Assert.IsType<ApprovalEvent>(ProgramParser.Parse("approval default deny")[0]);
        Assert.Equal("default", e.Key);
        Assert.Equal("deny", e.Value);
    }

    [Fact]
    public void Approval_MissingValue_Throws()
    {
        Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("approval bash"));
    }

    [Fact]
    public void BackslashContinuation_JoinsWithNewline()
    {
        var e = Assert.IsType<UserEvent>(ProgramParser.Parse("user first line \\\nsecond line")[0]);
        Assert.Equal("first line\nsecond line", e.Text);
    }

    [Fact]
    public void BlankLines_ShebangAndComments_AreSkipped()
    {
        var src = "#!/usr/bin/env nb\n\n# a comment\nuser hello\n";
        var events = ProgramParser.Parse(src);
        var e = Assert.Single(events);
        Assert.Equal("hello", Assert.IsType<UserEvent>(e).Text);
    }

    [Fact]
    public void AtFile_ResolvesWholeContentThroughIncludeResolver()
    {
        var events = ProgramParser.Parse("system @base.md", path => path == "base.md" ? "RESOLVED PROMPT" : "?");
        Assert.Equal("RESOLVED PROMPT", Assert.IsType<SystemEvent>(events[0]).Text);
    }

    [Fact]
    public void AtFile_WithTrailingText_StaysLiteral()
    {
        var events = ProgramParser.Parse("user @base.md and more", _ => "SHOULD NOT BE USED");
        Assert.Equal("@base.md and more", Assert.IsType<UserEvent>(events[0]).Text);
    }

    [Fact]
    public void UnknownVerb_Throws_ListingKnownVerbs()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("frobnicate the widget"));
        Assert.Contains("frobnicate", ex.Message);
        Assert.Contains("provider", ex.Message);
    }

    [Fact]
    public void ProviderWithoutValue_Throws()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("provider"));
        Assert.Contains("requires a value", ex.Message);
    }

    [Fact]
    public void BareMcpToken_Throws()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("mcp figma"));
        Assert.Contains("+name", ex.Message);
    }

    [Fact]
    public void ToolCallInSource_PointsToBytecode()
    {
        var ex = Assert.Throws<ProgramParseException>(() => ProgramParser.Parse("tool_call bash"));
        Assert.Contains("JSONL", ex.Message);
    }

    [Fact]
    public void ParsedProgram_SerializesToBytecode()
    {
        var events = ProgramParser.Parse("provider anthropic\nrun hello");
        var jsonl = TranscriptSerializer.Serialize(events);
        Assert.Contains("\"type\":\"provider\"", jsonl);
        Assert.Contains("\"type\":\"run\"", jsonl);
        // And the bytecode parses back to the same shapes.
        var reparsed = TranscriptSerializer.Parse(jsonl);
        Assert.Equal("anthropic", Assert.IsType<ProviderEvent>(reparsed[0]).Name);
        Assert.Equal("hello", Assert.IsType<RunEvent>(reparsed[1]).Prompt);
    }
}
