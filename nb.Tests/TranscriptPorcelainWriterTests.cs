using System.Text.Json.Nodes;
using nb.Transcript;

namespace nb.Tests;

public class TranscriptPorcelainWriterTests
{
    private static JsonObject Args(string key, string value) => new() { [key] = value };

    [Fact]
    public void SkipsSystemAndUser_KeepsAssistantProseVerbatim()
    {
        var events = new List<TranscriptEvent>
        {
            new SystemEvent { Turn = 0, Text = "you are nb" },
            new UserEvent { Turn = 1, Text = "do the thing" },
            new AssistantTextEvent { Turn = 2, Text = "Here:\n```json\n{\"ok\":true}\n```" },
        };

        var text = TranscriptPorcelainWriter.Write(events);

        Assert.DoesNotContain("you are nb", text);
        Assert.DoesNotContain("do the thing", text);
        // Fence survives byte-for-byte (real newlines, not escaped).
        Assert.Contains("```json\n{\"ok\":true}\n```", text);
    }

    [Fact]
    public void ToolCall_RendersPrimaryArgInline()
    {
        var events = new List<TranscriptEvent>
        {
            new ToolCallEvent { Turn = 1, Id = "a", Name = "bash", Arguments = Args("command", "touch x.txt") },
            new ToolCallEvent { Turn = 1, Id = "b", Name = "list_dir", Arguments = Args("path", ".") },
        };

        var text = TranscriptPorcelainWriter.Write(events);

        Assert.Contains("TOOL bash touch x.txt\n", text);
        Assert.Contains("TOOL list_dir .\n", text);
    }

    [Fact]
    public void ToolCall_FallsBackToCompactJson_WhenNoPrimaryArg()
    {
        var events = new List<TranscriptEvent>
        {
            new ToolCallEvent { Turn = 1, Id = "a", Name = "weird", Arguments = new JsonObject { ["foo"] = "bar" } },
        };

        Assert.Contains("TOOL weird {\"foo\":\"bar\"}\n", TranscriptPorcelainWriter.Write(events));
    }

    [Fact]
    public void ToolResult_IsEscapedToOneLine()
    {
        var events = new List<TranscriptEvent>
        {
            new ToolResultEvent { Turn = 1, Id = "a", Output = "hi\n\n[exit code: 0]" },
        };

        var text = TranscriptPorcelainWriter.Write(events);

        Assert.Equal("RESULT hi\\n\\n[exit code: 0]\n", text);
        // Exactly one line (plus the trailing newline).
        Assert.Single(text.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void Trailer_ReportsReasonAndCounts()
    {
        var trailer = new ResultEvent
        {
            ExitReason = "ok",
            Turns = 2,
            ToolCalls = 1,
            Usage = new UsageInfo { Input = 10, Output = 20, Total = 30 },
        };

        var line = TranscriptPorcelainWriter.Trailer(trailer);

        Assert.Equal("nb: exit_reason=ok turns=2 tool_calls=1 input=10 output=20 total=30", line);
    }
}
