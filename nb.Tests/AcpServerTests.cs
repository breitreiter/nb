using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using nb.Acp;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// <c>nb --acp</c> (<c>plans/atif-and-acp.md</c> stage 5), driven over in-memory pipes as
/// acpx drives it: initialize, one session/new, prompts, a cancel. The shapes asserted are
/// the ACP schema's (<c>@agentclientprotocol/sdk</c> 0.28), which acpx validates against.
/// </summary>
public class AcpServerTests
{
    [Fact]
    public async Task Initialize_AnswersProtocolOne_AndNamesNb()
    {
        await using var client = new Client(Server([]));
        var result = (await client.Request("initialize", new JsonObject { ["protocolVersion"] = 1 }))["result"]!;

        Assert.Equal(1, (int)result["protocolVersion"]!);
        Assert.Equal("nb", (string)result["agentInfo"]!["name"]!);
        Assert.False((bool)result["agentCapabilities"]!["loadSession"]!);
        Assert.Empty(result["authMethods"]!.AsArray());
    }

    [Fact]
    public async Task APrompt_StreamsTheTurn_AndEndsIt()
    {
        await using var client = new Client(Server([new ApprovalEvent { Key = "bash", Value = "echo hi" }]));
        var session = await client.NewSession();

        var response = await client.Prompt(session, "MOCK:tool=bash echo hi");

        Assert.Equal("end_turn", (string)response["result"]!["stopReason"]!);
        Assert.Equal("ok", (string)response["result"]!["_meta"]!["nb"]!["exit_reason"]!);
        Assert.Equal(30, (long)response["result"]!["usage"]!["totalTokens"]!);
        Assert.All(client.Updates, u => Assert.Equal(session, (string)u["params"]!["sessionId"]!));

        // The prompt is not echoed; the call goes pending → in_progress → completed; then the answer.
        Assert.Equal(["tool_call", "tool_call_update", "tool_call_update", "agent_message_chunk"], client.Kinds());
        var call = client.Update(0);
        Assert.Equal(("bash", "execute", "pending"), ((string)call["title"]!, (string)call["kind"]!, (string)call["status"]!));
        Assert.Equal("echo hi", (string)call["rawInput"]!["command"]!);
        Assert.Equal("in_progress", (string)client.Update(1)["status"]!);
        var done = client.Update(2);
        Assert.Equal("completed", (string)done["status"]!);
        Assert.Contains("hi", (string)done["content"]![0]!["content"]!["text"]!);
        Assert.Equal("OK", (string)client.Update(3)["content"]!["text"]!);
    }

    [Fact]
    public async Task ADeniedCall_Fails()
    {
        await using var client = new Client(Server([]));
        var session = await client.NewSession();

        await client.Prompt(session, "MOCK:tool=bash echo changed > note.txt");

        Assert.Equal(["tool_call", "tool_call_update", "agent_message_chunk"], client.Kinds());
        Assert.Equal("failed", (string)client.Update(1)["status"]!);
    }

    // Two prompts are two runs of one program: the token budget is the session's, so the
    // second prompt finds it spent and makes no model call at all.
    [Fact]
    public async Task TwoPrompts_ShareTheSessionsBudgets()
    {
        await using var client = new Client(Server([new BudgetEvent { Key = "tokens", Value = 15 }]));
        var session = await client.NewSession();

        var first = await client.Prompt(session, "hello");
        var second = await client.Prompt(session, "hello again");

        Assert.Equal("end_turn", (string)first["result"]!["stopReason"]!);
        Assert.Equal("max_tokens", (string)second["result"]!["stopReason"]!);
        Assert.Equal(0, (long)second["result"]!["usage"]!["totalTokens"]!);
    }

    // The client's think time between prompts is not the run's.
    [Fact]
    public async Task TheWallBudget_DoesNotRunBetweenPrompts()
    {
        await using var client = new Client(Server([new BudgetEvent { Key = "wall_ms", Value = 1500 }]));
        var session = await client.NewSession();

        await client.Prompt(session, "hello");
        await Task.Delay(2000);
        var second = await client.Prompt(session, "hello again");

        Assert.Equal("ok", (string)second["result"]!["_meta"]!["nb"]!["exit_reason"]!);
    }

    [Fact]
    public async Task Cancel_StopsTheRunningCommand_AndAnswersCancelled()
    {
        await using var client = new Client(Server([new ApprovalEvent { Key = "bash", Value = "sleep *" }]));
        var session = await client.NewSession();
        var clock = Stopwatch.StartNew();

        var id = client.Send("session/prompt", PromptParams(session, "MOCK:tool=bash sleep 30"));
        await client.ReadUntil(m => (string?)m["params"]?["update"]?["status"] == "in_progress");
        client.Notify("session/cancel", new JsonObject { ["sessionId"] = session });
        var response = await client.ReadUntil(m => (int?)m["id"] == id);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
        Assert.Equal("cancelled", (string)response["result"]!["stopReason"]!);
        Assert.Equal("failed", (string)client.Updates.Last()["params"]!["update"]!["status"]!);
    }

    [Fact]
    public async Task AFailedRun_IsAnError_CarryingTheExitReason()
    {
        await using var client = new Client(Server([], ("ChatProviders:0:MaxRetries", "0")));
        var session = await client.NewSession();

        var response = await client.Prompt(session, "MOCK:ratelimit");

        Assert.Null(response["result"]);
        Assert.Equal("rate_limited", (string)response["error"]!["data"]!["exit_reason"]!);
    }

    [Fact]
    public async Task OneSessionPerProcess()
    {
        await using var client = new Client(Server([]));
        await client.NewSession();

        var second = await client.Request("session/new", NewSessionParams());

        Assert.Equal(-32600, (int)second["error"]!["code"]!);
    }

    [Fact]
    public async Task APromptForAnotherSession_IsRefused()
    {
        await using var client = new Client(Server([]));
        await client.NewSession();

        var response = await client.Prompt("not-this-one", "hello");

        Assert.Equal(-32602, (int)response["error"]!["code"]!);
    }

    [Fact]
    public async Task EachPrompt_RewritesTheTrajectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nb-acp-{Guid.NewGuid():N}.json");
        try
        {
            await using var client = new Client(Server([], trajectory: path));
            var session = await client.NewSession();

            await client.Prompt(session, "hello");
            var first = JsonNode.Parse(File.ReadAllText(path))!;
            await client.Prompt(session, "hello again");
            var second = JsonNode.Parse(File.ReadAllText(path))!;

            Assert.Equal(2, first["steps"]!.AsArray().Count);
            Assert.Equal(["user", "agent", "user", "agent"], second["steps"]!.AsArray().Select(s => (string)s!["source"]!));
            Assert.Equal(AtifWriter.SchemaVersion, (string)second["schema_version"]!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AProgramWithARun_IsRefused()
    {
        Assert.Null(AcpServer.RunRefusal([new BudgetEvent { Key = "tokens", Value = 5 }]));
        Assert.NotNull(AcpServer.RunRefusal([new RunEvent { Prompt = "hi" }]));
    }

    [Theory]
    [InlineData(ExitReasons.Ok, "end_turn")]
    [InlineData(ExitReasons.OracleMiss, "end_turn")]
    [InlineData(ExitReasons.TimeBudget, "end_turn")]
    [InlineData(ExitReasons.ApprovalDenied, "end_turn")]
    [InlineData(ExitReasons.MaxToolCalls, "max_turn_requests")]
    [InlineData(ExitReasons.ToolErrorLimit, "max_turn_requests")]
    [InlineData(ExitReasons.OracleBudget, "max_turn_requests")]
    [InlineData(ExitReasons.TokenBudget, "max_tokens")]
    [InlineData(ExitReasons.Cancelled, "cancelled")]
    [InlineData(ExitReasons.ProviderError, null)]
    [InlineData(ExitReasons.RateLimited, null)]
    public void ExitReasons_MapToStopReasons(string exitReason, string? stopReason) =>
        Assert.Equal(stopReason, AcpServer.StopReason(exitReason));

    [Fact]
    public void APromptsBlocks_BecomeOneUserMessage()
    {
        var message = AcpServer.ToMessage(new JsonArray(
            new JsonObject { ["type"] = "text", ["text"] = "look" },
            new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String([1, 2]), ["mimeType"] = "image/png" },
            new JsonObject { ["type"] = "resource_link", ["uri"] = "file:///a.txt", ["name"] = "a.txt" },
            new JsonObject { ["type"] = "audio", ["data"] = "", ["mimeType"] = "audio/wav" }))!;

        Assert.Equal(3, message.Contents.Count);
        Assert.Null(AcpServer.ToMessage(new JsonArray(new JsonObject { ["type"] = "audio" })));
    }

    // ---- Helpers

    private static JsonObject NewSessionParams() => new() { ["cwd"] = Directory.GetCurrentDirectory(), ["mcpServers"] = new JsonArray() };

    private static JsonObject PromptParams(string session, string text) => new()
    {
        ["sessionId"] = session,
        ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    private static Func<TextWriter, AcpServer> Server(IReadOnlyList<TranscriptEvent> program, (string, string)? setting = null, string? trajectory = null) =>
        output => new AcpServer(MockConfig(setting), program, Options(), output, TextWriter.Null, trajectory);

    /// <summary>An ACP client on in-memory pipes: one line per message each way, as on stdio.</summary>
    private sealed class Client : IAsyncDisposable
    {
        private readonly Channel<string> _toServer = Channel.CreateUnbounded<string>();
        private readonly Channel<string> _fromServer = Channel.CreateUnbounded<string>();
        private readonly Task _serving;
        private int _nextId;

        public List<JsonObject> Updates { get; } = new();

        public Client(Func<TextWriter, AcpServer> server) =>
            _serving = server(new LineWriter(_fromServer.Writer)).ServeAsync(new LineReader(_toServer.Reader));

        public int Send(string method, JsonObject @params)
        {
            var id = ++_nextId;
            _toServer.Writer.TryWrite(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }.ToJsonString());
            return id;
        }

        public void Notify(string method, JsonObject @params) =>
            _toServer.Writer.TryWrite(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params }.ToJsonString());

        public async Task<JsonObject> Request(string method, JsonObject @params)
        {
            var id = Send(method, @params);
            return await ReadUntil(m => (int?)m["id"] == id);
        }

        public async Task<string> NewSession()
        {
            await Request("initialize", new JsonObject { ["protocolVersion"] = 1 });
            return (string)(await Request("session/new", NewSessionParams()))["result"]!["sessionId"]!;
        }

        public async Task<JsonObject> Prompt(string session, string text)
        {
            Updates.Clear();
            return await Request("session/prompt", PromptParams(session, text));
        }

        // Every message is a JSON-RPC 2.0 object; updates are kept as they pass.
        public async Task<JsonObject> ReadUntil(Func<JsonObject, bool> match)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var message = JsonNode.Parse(await _fromServer.Reader.ReadAsync(timeout.Token))!.AsObject();
                Assert.Equal("2.0", (string)message["jsonrpc"]!);
                if ((string?)message["method"] == "session/update") Updates.Add(message);
                if (match(message)) return message;
            }
        }

        public JsonObject Update(int i) => Updates[i]["params"]!["update"]!.AsObject();

        public List<string> Kinds() => Updates.Select(u => (string)u["params"]!["update"]!["sessionUpdate"]!).ToList();

        public async ValueTask DisposeAsync()
        {
            _toServer.Writer.Complete();
            await _serving;
        }
    }

    private sealed class LineReader(ChannelReader<string> lines) : TextReader
    {
        public override async Task<string?> ReadLineAsync() =>
            await lines.WaitToReadAsync() && lines.TryRead(out var line) ? line : null;
    }

    private sealed class LineWriter(ChannelWriter<string> lines) : TextWriter
    {
        private readonly StringBuilder _line = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value != '\n') { _line.Append(value); return; }
            lines.TryWrite(_line.ToString());
            _line.Clear();
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? "") Write(c);
        }
    }

    private static IConfiguration MockConfig((string Key, string Value)? setting = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["Trust"] = "false",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
        };
        if (setting is { } s) values[s.Key] = s.Value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }
}
