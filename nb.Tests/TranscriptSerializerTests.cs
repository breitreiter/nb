using System.Text.Json;
using System.Text.Json.Nodes;
using nb.Transcript;

namespace nb.Tests;

public class TranscriptSerializerTests
{
    // The worked example from plans/transcript-schema.md ("the captured tool run
    // as jsonl") — the golden fixture for the round-trip contract.
    private const string GoldenWorkedExample =
        """
        {"type":"user","turn":0,"text":"Use the bash tool to run 'echo hi', then use list_dir on '.', then tell me how many entries. Keep it terse."}
        {"type":"tool_call","turn":1,"id":"ncqT01","name":"bash","arguments":{"command":"echo hi","description":"Run echo hi","timeout_seconds":10},"approved":"auto"}
        {"type":"tool_call","turn":1,"id":"yhn702","name":"list_dir","arguments":{"path":"."},"approved":"auto"}
        {"type":"tool_call","turn":1,"id":"PEh103","name":"bash","arguments":{"command":"ls -1 | wc -l","description":"Count entries","timeout_seconds":10},"approved":"auto"}
        {"type":"tool_result","turn":1,"id":"ncqT01","output":"hi\n\n[exit code: 0]","result":{"exit_code":0,"truncated":false}}
        {"type":"tool_result","turn":1,"id":"yhn702","output":"[dir]  prompts\n[file] appsettings.json","result":{"entries":55}}
        {"type":"tool_result","turn":1,"id":"PEh103","output":"54\n\n[exit code: 0]","result":{"exit_code":0,"truncated":false}}
        {"type":"assistant_text","turn":2,"text":"hi\n\nThere are 54 entries in the current directory."}
        {"type":"result","turn":null,"exit_reason":"ok","usage":{"input":812,"output":143,"total":955},"turns":2,"tool_calls":3}
        """;

    [Fact]
    public void WorkedExample_ParsesAllEvents_InOrder()
    {
        var events = TranscriptSerializer.Parse(GoldenWorkedExample);

        Assert.Equal(9, events.Count);
        Assert.IsType<UserEvent>(events[0]);
        Assert.IsType<ToolCallEvent>(events[1]);
        Assert.IsType<ToolCallEvent>(events[2]);
        Assert.IsType<ToolCallEvent>(events[3]);
        Assert.IsType<ToolResultEvent>(events[4]);
        Assert.IsType<ToolResultEvent>(events[5]);
        Assert.IsType<ToolResultEvent>(events[6]);
        Assert.IsType<AssistantTextEvent>(events[7]);
        Assert.IsType<ResultEvent>(events[8]);
    }

    [Fact]
    public void WorkedExample_KeyFieldsDecoded()
    {
        var events = TranscriptSerializer.Parse(GoldenWorkedExample);

        var user = Assert.IsType<UserEvent>(events[0]);
        Assert.Equal(0, user.Turn);
        Assert.StartsWith("Use the bash tool", user.Text);

        var call = Assert.IsType<ToolCallEvent>(events[1]);
        Assert.Equal(1, call.Turn);
        Assert.Equal("ncqT01", call.Id);
        Assert.Equal("bash", call.Name);
        Assert.Equal("auto", call.Approved);
        Assert.Equal("echo hi", call.Arguments!["command"]!.GetValue<string>());

        // CallId is the only join key — result matches its call by id.
        var result = Assert.IsType<ToolResultEvent>(events[4]);
        Assert.Equal("ncqT01", result.Id);
        Assert.Equal("hi\n\n[exit code: 0]", result.Output);
        Assert.Equal(0, result.Result!["exit_code"]!.GetValue<int>());

        var trailer = Assert.IsType<ResultEvent>(events[8]);
        Assert.Null(trailer.Turn); // run-level, not a message
        Assert.Equal("ok", trailer.ExitReason);
        Assert.Equal(955, trailer.Usage!.Total);
        Assert.Equal(2, trailer.Turns);
        Assert.Equal(3, trailer.ToolCalls);
    }

    [Fact]
    public void WorkedExample_RoundTripsSemantically()
    {
        // Parse -> serialize -> compare each line by JSON value equality.
        var events = TranscriptSerializer.Parse(GoldenWorkedExample);
        var reemitted = TranscriptSerializer.Serialize(events);

        var originalLines = NonBlankLines(GoldenWorkedExample);
        var emittedLines = NonBlankLines(reemitted);

        Assert.Equal(originalLines.Length, emittedLines.Length);
        for (int i = 0; i < originalLines.Length; i++)
            AssertJsonEqual(originalLines[i], emittedLines[i], $"line {i + 1}");
    }

    [Fact]
    public void Serialize_IsIdempotent()
    {
        var once = TranscriptSerializer.Serialize(TranscriptSerializer.Parse(GoldenWorkedExample));
        var twice = TranscriptSerializer.Serialize(TranscriptSerializer.Parse(once));
        Assert.Equal(once, twice);
    }

    [Fact]
    public void PreservesJsonArgumentTypes_OnEmit()
    {
        // Decision 4: arguments keep their true JSON types; numbers stay numbers,
        // bools stay bools — no coercion to strings.
        var ev = new ToolCallEvent
        {
            Turn = 1,
            Id = "x",
            Name = "demo",
            Arguments = new JsonObject
            {
                ["count"] = 10,
                ["ratio"] = 1.5,
                ["flag"] = true,
                ["label"] = "hi",
            },
        };

        var line = TranscriptSerializer.SerializeEvent(ev);

        Assert.Contains("\"count\":10", line);
        Assert.Contains("\"ratio\":1.5", line);
        Assert.Contains("\"flag\":true", line);
        Assert.Contains("\"label\":\"hi\"", line);
        Assert.DoesNotContain("\"count\":\"10\"", line);
    }

    [Fact]
    public void PreservesJsonArgumentTypes_ThroughParseAndReemit()
    {
        // The load-time string-coercion quirk (ConversationManager.cs:1794) is gone:
        // a numeric argument read from jsonl re-emits as a number, not a string.
        const string line = """{"type":"tool_call","turn":1,"id":"x","name":"bash","arguments":{"timeout_seconds":10}}""";

        var ev = Assert.IsType<ToolCallEvent>(TranscriptSerializer.ParseLine(line, 1));
        Assert.Equal(JsonValueKind.Number, ev.Arguments!["timeout_seconds"]!.GetValueKind());

        var reemitted = TranscriptSerializer.SerializeEvent(ev);
        Assert.Contains("\"timeout_seconds\":10", reemitted);
    }

    [Fact]
    public void AllEventTypes_RoundTrip()
    {
        var events = new TranscriptEvent[]
        {
            new SystemEvent { Turn = 0, Text = "You are terse." },
            new UserEvent { Turn = 0, Text = "hi" },
            new ThinkingEvent { Turn = 1, Text = "let me think" },
            new AssistantTextEvent { Turn = 1, Text = "hello" },
            new AssistantJsonEvent { Turn = 1, Value = new JsonObject { ["ok"] = true, ["n"] = 3 } },
            new UserEvent
            {
                Turn = 2,
                Content = new ContentPart[]
                {
                    new TextPart { Text = "look at this" },
                    new ImagePart { MediaType = "image/png", Note = "[Image loaded: x.png]" },
                },
            },
            new RunEvent { Prompt = "go" },
            new ResultEvent
            {
                ExitReason = "ok",
                Usage = new UsageInfo { Input = 1, Output = 2, Total = 3 },
                Turns = 1,
                ToolCalls = 0,
                DurationMs = 4120,
            },
        };

        var jsonl = TranscriptSerializer.Serialize(events);
        var reparsed = TranscriptSerializer.Parse(jsonl);
        var reemitted = TranscriptSerializer.Serialize(reparsed);

        Assert.Equal(events.Length, reparsed.Count);
        Assert.Equal(jsonl, reemitted); // stable across a full round trip
    }

    [Fact]
    public void Multipart_ImageNote_RoundTrips()
    {
        var ev = new UserEvent
        {
            Turn = 0,
            Content = new ContentPart[]
            {
                new TextPart { Text = "caption" },
                new ImagePart { MediaType = "image/png", Data = "AAAA", Note = "[Image loaded: y.png]" },
            },
        };

        var parsed = Assert.IsType<UserEvent>(TranscriptSerializer.ParseLine(TranscriptSerializer.SerializeEvent(ev), 1));
        Assert.Null(parsed.Text);
        Assert.Equal(2, parsed.Content!.Count);
        var img = Assert.IsType<ImagePart>(parsed.Content[1]);
        Assert.Equal("image/png", img.MediaType);
        Assert.Equal("AAAA", img.Data);
        Assert.Equal("[Image loaded: y.png]", img.Note);
    }

    [Fact]
    public void UnknownType_SkippedWithWarning()
    {
        const string jsonl =
            """
            {"type":"user","turn":0,"text":"hi"}
            {"type":"future_event","turn":1,"whatever":true}
            {"type":"assistant_text","turn":1,"text":"ok"}
            """;

        var warnings = new List<string>();
        var events = TranscriptSerializer.Parse(jsonl, warnings);

        Assert.Equal(2, events.Count); // the unknown event dropped
        Assert.IsType<UserEvent>(events[0]);
        Assert.IsType<AssistantTextEvent>(events[1]);
        var warning = Assert.Single(warnings);
        Assert.Contains("future_event", warning);
        Assert.Contains("line 2", warning);
    }

    [Fact]
    public void BlankLines_Skipped()
    {
        var jsonl = "\n{\"type\":\"user\",\"turn\":0,\"text\":\"hi\"}\n\n   \n";
        var events = TranscriptSerializer.Parse(jsonl);
        Assert.Single(events);
    }

    [Fact]
    public void ToolResult_MissingOutput_Throws()
    {
        const string line = """{"type":"tool_result","turn":1,"id":"x"}""";
        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptSerializer.ParseLine(line, 7));
        Assert.Contains("output", ex.Message);
        Assert.Contains("line 7", ex.Message);
    }

    [Fact]
    public void ToolCall_MissingId_Throws()
    {
        const string line = """{"type":"tool_call","turn":1,"name":"bash"}""";
        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptSerializer.ParseLine(line, 3));
        Assert.Contains("id", ex.Message);
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptSerializer.ParseLine("{not json", 1));
        Assert.Contains("line 1", ex.Message);
    }

    [Fact]
    public void MissingType_Throws()
    {
        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptSerializer.ParseLine("""{"turn":0}""", 1));
        Assert.Contains("type", ex.Message);
    }

    // ---- config directives (Phase 3.1) ----

    // A small conversation-program mixing config directives, turns, and a run —
    // the bytecode the source syntax desugars to.
    private const string GoldenProgram =
        """
        {"type":"provider","turn":null,"name":"anthropic"}
        {"type":"model","turn":null,"name":"claude-sonnet-5"}
        {"type":"output","turn":null,"mode":"jsonl"}
        {"type":"mcp","turn":null,"add":["figma"],"remove":["tester"]}
        {"type":"tools","turn":null,"reset":true}
        {"type":"system","turn":0,"text":"you are terse"}
        {"type":"run","turn":1,"prompt":"the real task"}
        """;

    [Fact]
    public void Program_ParsesConfigDirectives_InOrder()
    {
        var events = TranscriptSerializer.Parse(GoldenProgram);

        Assert.Equal(7, events.Count);
        Assert.Equal("anthropic", Assert.IsType<ProviderEvent>(events[0]).Name);
        Assert.Equal("claude-sonnet-5", Assert.IsType<ModelEvent>(events[1]).Name);
        Assert.Equal("jsonl", Assert.IsType<OutputEvent>(events[2]).Mode);

        var mcp = Assert.IsType<McpEvent>(events[3]);
        Assert.Equal(new[] { "figma" }, mcp.Add);
        Assert.Equal(new[] { "tester" }, mcp.Remove);
        Assert.False(mcp.Reset);

        var tools = Assert.IsType<ToolsEvent>(events[4]);
        Assert.True(tools.Reset);
        Assert.Empty(tools.Add);

        Assert.IsType<SystemEvent>(events[5]);
        Assert.Equal("the real task", Assert.IsType<RunEvent>(events[6]).Prompt);
    }

    [Fact]
    public void ConfigDirectives_RoundTrip()
    {
        foreach (var line in NonBlankLines(GoldenProgram))
        {
            var reserialized = TranscriptSerializer.SerializeEvent(TranscriptSerializer.ParseLine(line, 1)!);
            AssertJsonEqual(line, reserialized, "config directive");
        }
    }

    [Fact]
    public void Mcp_OmitsEmptyDeltaFields()
    {
        var json = TranscriptSerializer.SerializeEvent(new McpEvent { Add = new[] { "figma" } });
        Assert.Contains("\"add\"", json);
        Assert.DoesNotContain("remove", json);
        Assert.DoesNotContain("reset", json);
    }

    [Fact]
    public void Provider_MissingName_Throws()
    {
        var ex = Assert.Throws<TranscriptFormatException>(
            () => TranscriptSerializer.ParseLine("""{"type":"provider","turn":null}""", 1));
        Assert.Contains("name", ex.Message);
    }

    // ---- helpers ----

    private static string[] NonBlankLines(string s) =>
        s.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

    private static void AssertJsonEqual(string expected, string actual, string because)
    {
        var e = JsonNode.Parse(expected);
        var a = JsonNode.Parse(actual);
        Assert.True(JsonNode.DeepEquals(e, a), $"{because}: JSON differs.\n  expected: {expected}\n  actual:   {actual}");
    }
}
