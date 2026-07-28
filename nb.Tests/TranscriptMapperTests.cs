using System.Text.Json;
using Microsoft.Extensions.AI;
using nb.Transcript;

namespace nb.Tests;

public class TranscriptMapperTests
{
    // The same captured run as the schema doc's worked example, expressed as the
    // ChatMessage history nb actually holds. Emitting it must reproduce the
    // example's CORE events (enrichment — approved/result/usage — is not in history).
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
    public void WorkedExample_MapsToCoreEvents_WithCorrectTurns()
    {
        var events = TranscriptMapper.FromHistory(WorkedExampleHistory());

        Assert.Equal(8, events.Count); // user + 3 calls + 3 results + assistant_text (no assistant_text for the calls message)

        var user = Assert.IsType<UserEvent>(events[0]);
        Assert.Equal(0, user.Turn);

        // The three calls share the assistant round's turn.
        for (int i = 1; i <= 3; i++)
        {
            var call = Assert.IsType<ToolCallEvent>(events[i]);
            Assert.Equal(1, call.Turn);
            Assert.Null(call.Approved); // enrichment absent from history
        }

        // Results inherit the calling assistant's turn — so the loader re-batches.
        for (int i = 4; i <= 6; i++)
        {
            var result = Assert.IsType<ToolResultEvent>(events[i]);
            Assert.Equal(1, result.Turn);
            Assert.Null(result.Result); // structured enrichment absent
        }

        var text = Assert.IsType<AssistantTextEvent>(events[7]);
        Assert.Equal(2, text.Turn);
        Assert.StartsWith("hi", text.Text);
    }

    [Fact]
    public void WorkedExample_JoinKeysAndOutputsPreserved()
    {
        var events = TranscriptMapper.FromHistory(WorkedExampleHistory());

        var firstCall = Assert.IsType<ToolCallEvent>(events[1]);
        Assert.Equal("ncqT01", firstCall.Id);
        Assert.Equal("bash", firstCall.Name);

        var firstResult = Assert.IsType<ToolResultEvent>(events[4]);
        Assert.Equal("ncqT01", firstResult.Id); // matches the call by id
        Assert.Equal("hi\n\n[exit code: 0]", firstResult.Output);
    }

    [Fact]
    public void MappedArguments_PreserveJsonTypes()
    {
        var events = TranscriptMapper.FromHistory(WorkedExampleHistory());
        var call = Assert.IsType<ToolCallEvent>(events[1]);

        // timeout_seconds was a boxed int 10 — must stay a JSON number, not a string.
        Assert.Equal(JsonValueKind.Number, call.Arguments!["timeout_seconds"]!.GetValueKind());
        Assert.Equal(10, call.Arguments["timeout_seconds"]!.GetValue<int>());
        Assert.Equal("echo hi", call.Arguments["command"]!.GetValue<string>());
    }

    [Fact]
    public void MappedEvents_SerializeAndRoundTrip()
    {
        var events = TranscriptMapper.FromHistory(WorkedExampleHistory());
        var jsonl = TranscriptSerializer.Serialize(events);
        var reparsed = TranscriptSerializer.Parse(jsonl);

        Assert.Equal(TranscriptSerializer.Serialize(events), TranscriptSerializer.Serialize(reparsed));
    }

    [Fact]
    public void MultiTurnConversation_AssignsDistinctTurns()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "one"),
            new(ChatRole.Assistant, "first"),
            new(ChatRole.User, "two"),
            new(ChatRole.Assistant, "second"),
        };

        var events = TranscriptMapper.FromHistory(history);

        Assert.Equal(new int?[] { 0, 1, 2, 3 }, events.Select(e => e.Turn));
    }

    [Fact]
    public void AssistantWithTextAndCalls_SharesTurn()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "go"),
            new(ChatRole.Assistant, new List<AIContent>
            {
                new TextContent("working on it"),
                new FunctionCallContent("c1", "bash", new Dictionary<string, object?> { ["command"] = "ls" }),
            }),
        };

        var events = TranscriptMapper.FromHistory(history);

        var text = Assert.IsType<AssistantTextEvent>(events[1]);
        var call = Assert.IsType<ToolCallEvent>(events[2]);
        Assert.Equal(1, text.Turn);
        Assert.Equal(1, call.Turn); // text and calls of one message share a turn
    }

    [Fact]
    public void SystemMessage_MapsToSystemEvent()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "You are terse."),
            new(ChatRole.User, "hi"),
        };

        var events = TranscriptMapper.FromHistory(history);

        var sys = Assert.IsType<SystemEvent>(events[0]);
        Assert.Equal("You are terse.", sys.Text);
        Assert.Equal(0, sys.Turn);
        Assert.Equal(1, events[1].Turn); // each non-tool message gets its own turn; user follows system
    }

    [Fact]
    public void ImageResult_ExtractsTextNoteAsOutput()
    {
        // read_file returns a text note + image DataContent; the model-facing
        // output string is the text note.
        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "read the image"),
            new(ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("c1", "read_file", new Dictionary<string, object?> { ["path"] = "x.png" }) }),
            new(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("c1", new List<AIContent>
                {
                    new TextContent("[Image loaded: x.png]"),
                    new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
                }),
            }),
        };

        var events = TranscriptMapper.FromHistory(history);
        var result = Assert.IsType<ToolResultEvent>(events[events.Count - 1]);
        Assert.Equal("[Image loaded: x.png]", result.Output);
    }

    [Fact]
    public void ResultTrailer_DerivesCountsFromEvents()
    {
        var events = TranscriptMapper.FromHistory(WorkedExampleHistory());
        var trailer = TranscriptMapper.ResultTrailer(events, usage: new UsageInfo { Input = 812, Output = 143, Total = 955 });

        Assert.Equal(2, trailer.Turns);       // max turn reached
        Assert.Equal(3, trailer.ToolCalls);   // three tool_call events
        Assert.Equal(955, trailer.Usage!.Total);
        Assert.Equal("ok", trailer.ExitReason);
        Assert.Null(trailer.Turn);            // run-level, not a message
    }
}
