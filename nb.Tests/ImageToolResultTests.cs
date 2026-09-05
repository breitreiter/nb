using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using nb.Harness;
using nb.MCP;
using nb.Shell;

namespace nb.Tests;

/// <summary>
/// An image read by `read_file` has to leave nb as an image
/// (bugs/Image_Silently_Dropped_In_Tool_Results.md). It rode inside a
/// <c>FunctionResultContent</c> on a tool-role message, and the OpenAI chat-completions
/// wire format has no representation for an image there — image parts are valid only on
/// user messages. So the adapter dropped the pixels and kept the text note, and that note
/// carries the *filename*, which is what the model then answered from: renaming the same
/// 8×8 red PNG changed the answer from "red" to "White", while the model asserted it could
/// see the image.
///
/// <para>These assert on what nb hands its <c>IChatClient</c> — nb's actual output, and
/// the last point the shape is nb's responsibility. That an image on a *user* message
/// serializes correctly is the part of the format that has never been in doubt; it is the
/// tool-role placement that had no valid encoding.</para>
/// </summary>
public class ImageToolResultTests
{
    // The report's fixture: 8x8, entirely pure red, 74 bytes. No colour hint may appear in
    // the filename — with one, the model guesses correctly and masks the bug.
    private const string RedPng8x8 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mP4z8CAFTEMLQkAKP8/wc53yE8AAAAASUVORK5CYII=";

    [Fact]
    public async Task ImageFromReadFile_RidesAUserMessage_NotTheToolResult()
    {
        var (client, _) = await RunWithImageRead();

        var followUp = client.SecondRequest!;
        var toolMessage = followUp.Last(m => m.Role == ChatRole.Tool);
        var afterTool = followUp.SkipWhile(m => m != toolMessage).Skip(1).ToList();

        // The pixels are on a user message...
        var userWithImage = afterTool.FirstOrDefault(m =>
            m.Role == ChatRole.User && m.Contents.OfType<DataContent>().Any());
        Assert.NotNull(userWithImage);

        // ...and that message comes after the tool result, before the next assistant turn,
        // which is the ordering the format requires.
        Assert.DoesNotContain(afterTool.TakeWhile(m => m != userWithImage), m => m.Role == ChatRole.Assistant);
    }

    [Fact]
    public async Task ToolResult_NoLongerCarriesThePixels()
    {
        var (client, _) = await RunWithImageRead();

        var toolMessage = client.SecondRequest!.Last(m => m.Role == ChatRole.Tool);
        var parts = toolMessage.Contents.OfType<FunctionResultContent>()
            .SelectMany(f => f.Result as IEnumerable<AIContent> ?? Array.Empty<AIContent>())
            .ToList();

        Assert.DoesNotContain(parts, p => p is DataContent);
        // A text note stays, so the model knows the read succeeded and where the image went.
        Assert.Contains(parts.OfType<TextContent>(), t => t.Text.Contains("sample.png"));
    }

    [Fact]
    public async Task TheImageOnTheWire_IsTheBytesThatWereRead()
    {
        var (client, expected) = await RunWithImageRead();

        var data = client.SecondRequest!
            .Where(m => m.Role == ChatRole.User)
            .SelectMany(m => m.Contents.OfType<DataContent>())
            .Single();

        Assert.Equal("image/png", data.MediaType);
        Assert.Equal(expected, data.Data.ToArray());
    }

    [Fact]
    public async Task TextFileRead_AddsNoUserMessage()
    {
        // The split is for content the tool role cannot carry. An ordinary read must not
        // grow an extra turn.
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "notes.txt");
            await File.WriteAllTextAsync(path, "hello");

            var client = new ScriptedToolClient(path);
            await Manager(client, dir.FullName).RunAsync("read it");

            var followUp = client.SecondRequest!;
            var toolMessage = followUp.Last(m => m.Role == ChatRole.Tool);
            Assert.DoesNotContain(followUp.SkipWhile(m => m != toolMessage).Skip(1), m => m.Role == ChatRole.User);
        }
        finally { dir.Delete(true); }
    }

    private static async Task<(ScriptedToolClient Client, byte[] Bytes)> RunWithImageRead()
    {
        var dir = Directory.CreateTempSubdirectory();
        var bytes = Convert.FromBase64String(RedPng8x8);
        var path = Path.Combine(dir.FullName, "sample.png");
        await File.WriteAllBytesAsync(path, bytes);

        var client = new ScriptedToolClient(path);
        await Manager(client, dir.FullName).RunAsync("describe it");
        Assert.NotNull(client.SecondRequest);
        return (client, bytes);
    }

    private static ConversationManager Manager(IChatClient client, string cwd)
    {
        var env = ShellEnvironment.Detect(Array.Empty<string>());
        env.SetCwd(cwd);
        return new ConversationManager(client, new McpManager(), new FakeToolManager(),
            new NbHarness(readFile: new ReadFileTool(env)),
            new ApprovalPolicy(trust: false, new ApprovalPatterns(), _ => false));
    }

    /// <summary>Calls read_file once, then answers, capturing the follow-up request.</summary>
    private sealed class ScriptedToolClient(string path) : IChatClient
    {
        private int _calls;

        public IReadOnlyList<ChatMessage>? SecondRequest { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;

            if (++_calls == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    new List<AIContent>
                    {
                        new FunctionCallContent("call-1", "read_file",
                            new Dictionary<string, object?> { ["path"] = path }),
                    });
                yield break;
            }

            SecondRequest ??= messages.ToList();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "done");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
