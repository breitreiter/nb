using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using nb.Transcript;

namespace nb.Tests;

public class TranscriptLoaderTests
{
    private static List<ChatMessage> WorkedExampleHistory() => new()
    {
        new ChatMessage(ChatRole.User, "Use the bash tool to run 'echo hi', then use list_dir on '.', then tell me how many entries. Keep it terse."),
        new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("ncqT01", "bash", new Dictionary<string, object?> { ["command"] = "echo hi", ["description"] = "Run echo hi", ["timeout_seconds"] = 10 }),
            new FunctionCallContent("yhn702", "list_dir", new Dictionary<string, object?> { ["path"] = "." }),
            new FunctionCallContent("PEh103", "bash", new Dictionary<string, object?> { ["command"] = "ls -1 | wc -l", ["description"] = "Count entries", ["timeout_seconds"] = 10 }),
        }),
        new ChatMessage(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("ncqT01", "hi\n\n[exit code: 0]"),
            new FunctionResultContent("yhn702", "[dir]  prompts\n[file] appsettings.json"),
            new FunctionResultContent("PEh103", "54\n\n[exit code: 0]"),
        }),
        new ChatMessage(ChatRole.Assistant, "hi\n\nThere are 54 entries in the current directory."),
    };

    [Fact]
    public void WorkedExample_RebuildsRepresentationA()
    {
        var jsonl = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(WorkedExampleHistory()));
        var messages = TranscriptLoader.Load(jsonl);

        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);

        Assert.Equal(ChatRole.Assistant, messages[1].Role);
        var calls = messages[1].Contents.OfType<FunctionCallContent>().ToList();
        Assert.Equal(3, calls.Count);
        Assert.Equal("ncqT01", calls[0].CallId);
        Assert.Equal("bash", calls[0].Name);

        Assert.Equal(ChatRole.Tool, messages[2].Role);
        var toolResults = messages[2].Contents.OfType<FunctionResultContent>().ToList();
        Assert.Equal(3, toolResults.Count);
        Assert.Equal("ncqT01", toolResults[0].CallId);
        Assert.Equal("hi\n\n[exit code: 0]", toolResults[0].Result?.ToString());

        Assert.Equal(ChatRole.Assistant, messages[3].Role);
        Assert.StartsWith("hi", messages[3].Text);
    }

    [Fact]
    public void RoundTrip_EmitLoadEmit_IsStable()
    {
        // The schema's central promise: emit and load are inverse over core events.
        // history -> events -> jsonl -> history' -> events' -> jsonl' , assert jsonl == jsonl'.
        var jsonl = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(WorkedExampleHistory()));
        var reloaded = TranscriptLoader.Load(jsonl);
        var jsonl2 = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(reloaded));

        Assert.Equal(jsonl, jsonl2);
    }

    [Fact]
    public void Enrichment_IgnoredOnLoad()
    {
        // A full output transcript (thinking, approved, structured result, trailer)
        // loads to exactly the same history as its core subset.
        const string enriched =
            """
            {"type":"user","turn":0,"text":"hi"}
            {"type":"thinking","turn":1,"text":"pondering"}
            {"type":"tool_call","turn":1,"id":"c1","name":"bash","arguments":{"command":"ls"},"approved":"auto"}
            {"type":"tool_result","turn":1,"id":"c1","output":"a\nb","result":{"exit_code":0}}
            {"type":"assistant_text","turn":2,"text":"done"}
            {"type":"assistant_json","turn":2,"value":{"ok":true}}
            {"type":"result","turn":null,"exit_reason":"ok","usage":{"input":1,"output":2,"total":3}}
            """;

        var messages = TranscriptLoader.Load(enriched);

        // user, assistant(call), tool(result), assistant(text) — thinking / json / trailer dropped.
        Assert.Equal(4, messages.Count);
        Assert.Equal(ChatRole.User, messages[0].Role);
        Assert.Single(messages[1].Contents.OfType<FunctionCallContent>());
        Assert.Equal(ChatRole.Tool, messages[2].Role);
        Assert.Equal("done", messages[3].Text);
    }

    [Fact]
    public void PreservesArgumentTypes_ThroughLoad()
    {
        var jsonl = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(WorkedExampleHistory()));
        var reloaded = TranscriptLoader.Load(jsonl);
        var reevents = TranscriptMapper.FromHistory(reloaded);

        var call = Assert.IsType<ToolCallEvent>(reevents[1]);
        // numeric arg survives history reconstruction as a JSON number, not a string.
        Assert.Equal(JsonValueKind.Number, call.Arguments!["timeout_seconds"]!.GetValueKind());
        Assert.Equal(10, call.Arguments["timeout_seconds"]!.GetValue<int>());
    }

    [Fact]
    public void SystemMessage_RoundTrips()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are terse."),
            new(ChatRole.User, "hi"),
        };
        var jsonl = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(history));

        var reloaded = TranscriptLoader.Load(jsonl);

        Assert.Equal(ChatRole.System, reloaded[0].Role);
        Assert.Equal("You are terse.", reloaded[0].Text);
    }

    [Fact]
    public void ImageEvent_LoadsAsTextNote()
    {
        const string jsonl =
            """
            {"type":"user","turn":0,"content":[{"kind":"text","text":"see this"},{"kind":"image","media_type":"image/png","note":"[Image loaded: x.png]"}]}
            """;

        var messages = TranscriptLoader.Load(jsonl);

        var texts = messages[0].Contents.OfType<TextContent>().Select(t => t.Text).ToList();
        Assert.Contains("see this", texts);
        Assert.Contains("[Image loaded: x.png]", texts); // image collapsed to its note
    }

    [Fact]
    public void OrphanToolResult_Throws()
    {
        const string jsonl =
            """
            {"type":"user","turn":0,"text":"hi"}
            {"type":"tool_result","turn":1,"id":"nope","output":"x"}
            """;

        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptLoader.Load(jsonl));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void UnansweredToolCall_Throws()
    {
        // Dangling call — the v1 completed-round requirement rejects it.
        const string jsonl =
            """
            {"type":"user","turn":0,"text":"hi"}
            {"type":"tool_call","turn":1,"id":"c1","name":"bash","arguments":{"command":"ls"}}
            """;

        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptLoader.Load(jsonl));
        Assert.Contains("completed round", ex.Message);
    }

    [Fact]
    public void NonMonotonicTurns_Throws()
    {
        const string jsonl =
            """
            {"type":"user","turn":5,"text":"hi"}
            {"type":"assistant_text","turn":2,"text":"back in time"}
            """;

        var ex = Assert.Throws<TranscriptFormatException>(() => TranscriptLoader.Load(jsonl));
        Assert.Contains("non-decreasing", ex.Message);
    }

    [Fact]
    public void MultiTurn_RebuildsAllMessages()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "one"),
            new(ChatRole.Assistant, "first"),
            new(ChatRole.User, "two"),
            new(ChatRole.Assistant, "second"),
        };
        var jsonl = TranscriptSerializer.Serialize(TranscriptMapper.FromHistory(history));

        var reloaded = TranscriptLoader.Load(jsonl);

        Assert.Equal(4, reloaded.Count);
        Assert.Equal(new[] { ChatRole.User, ChatRole.Assistant, ChatRole.User, ChatRole.Assistant }, reloaded.Select(m => m.Role));
        Assert.Equal("second", reloaded[3].Text);
    }
}
