using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb.MCP;
using nb.Shell;
using nb.Transcript;

namespace nb.Acp;

/// <summary>
/// nb as an ACP agent (Agent Client Protocol v1, JSON-RPC 2.0, one message per line):
/// <c>nb --acp &lt;program&gt;</c>. The program sets the session up (provider, model,
/// approval, budgets, oracle, fabricated history) and holds no <c>run</c>; each
/// <c>session/prompt</c> is one, and the journal streams to the client as
/// <c>session/update</c>s while it happens (<c>plans/atif-and-acp.md</c> stage 5).
///
/// One session per process, as nb is one run per process. Hand-rolled: the agent side is
/// five methods and one notification, and a public protocol surface shouldn't hang on an
/// unofficial package. Shapes follow the schema of <c>@agentclientprotocol/sdk</c> 0.28,
/// what acpx (Harbor's bridge client) validates against.
/// </summary>
public sealed class AcpServer
{
    public const int ProtocolVersion = 1;

    // JSON-RPC 2.0's own codes.
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    private readonly IConfiguration _config;
    private readonly IReadOnlyList<TranscriptEvent> _program;
    private readonly NbOptions _options;
    private readonly TextWriter _out;
    private readonly TextWriter _log;
    private readonly string? _trajectoryPath;
    private readonly object _writeLock = new();

    private NbSession? _session;
    private string? _sessionId;
    private Task? _prompt;
    private CancellationTokenSource? _promptCts;
    private readonly HashSet<string> _deniedCalls = new();
    private readonly HashSet<string> _warningsShown = new();

    /// <param name="program">Directives only: a <c>run</c> is refused before this is built (<see cref="RunRefusal"/>).</param>
    /// <param name="output">The protocol stream. Nothing else may write to it.</param>
    /// <param name="log">Where nb's own notes go (stderr in the CLI).</param>
    /// <param name="trajectoryPath">Rewritten as ATIF at the end of every prompt, when set.</param>
    public AcpServer(IConfiguration config, IReadOnlyList<TranscriptEvent> program, NbOptions options,
        TextWriter output, TextWriter log, string? trajectoryPath = null)
    {
        _config = config;
        _program = program;
        _options = options;
        _out = output;
        _log = log;
        _trajectoryPath = trajectoryPath;
    }

    /// <summary>Why <paramref name="program"/> can't configure an ACP session, or null if it can.</summary>
    public static string? RunRefusal(IReadOnlyList<TranscriptEvent> program) =>
        program.Any(e => e is RunEvent)
            ? "--acp takes a program with no `run`: its prompts arrive over the protocol, as session/prompt."
            : null;

    /// <summary>
    /// Serve until <paramref name="input"/> ends or <paramref name="shutdown"/> fires. A prompt
    /// in flight is then cancelled and answered <c>cancelled</c>, and its trajectory written.
    /// </summary>
    public async Task ServeAsync(TextReader input, CancellationToken shutdown = default)
    {
        var stop = Task.Delay(Timeout.Infinite, shutdown);
        try
        {
            while (await ReadLineAsync(input, stop) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                await HandleAsync(line);
            }
        }
        finally
        {
            _promptCts?.Cancel();
            if (_prompt is not null) await _prompt;
            _session?.Dispose();
        }
    }

    // Raced rather than passed a token: a read on a console pipe need not honour one, and
    // a shutdown must not wait on a client that has stopped talking.
    private static async Task<string?> ReadLineAsync(TextReader input, Task stop)
    {
        var read = input.ReadLineAsync();
        return await Task.WhenAny(read, stop) == read ? await read : null;
    }

    private async Task HandleAsync(string line)
    {
        JsonObject message;
        try { message = JsonNode.Parse(line)?.AsObject() ?? throw new JsonException("not an object"); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            SendError(null, ParseError, $"not a JSON-RPC message: {ex.Message}");
            return;
        }

        var id = message["id"]?.DeepClone();
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        var @params = message["params"] as JsonObject ?? new JsonObject();

        // A response to a request of ours: nb sends none, so there is nothing to match.
        if (method is null) return;

        // Notifications. session/cancel is the only one ACP gives an agent.
        if (id is null)
        {
            if (method == "session/cancel" && SessionMatches(@params)) _promptCts?.Cancel();
            return;
        }

        switch (method)
        {
            case "initialize":
                SendResult(id, Initialize());
                break;
            case "authenticate":
                // Keys come from config, as on every other surface; there is nothing to sign in to.
                SendResult(id, new JsonObject());
                break;
            case "session/new":
                await NewSessionAsync(id, @params);
                break;
            case "session/prompt":
                StartPrompt(id, @params);
                break;
            default:
                SendError(id, MethodNotFound, $"nb does not implement '{method}'.");
                break;
        }
    }

    private static JsonObject Initialize() => new()
    {
        // An agent answers with the version it speaks; a client that can't speak it disconnects.
        ["protocolVersion"] = ProtocolVersion,
        ["agentCapabilities"] = new JsonObject
        {
            ["loadSession"] = false,
            ["promptCapabilities"] = new JsonObject { ["image"] = true, ["audio"] = false, ["embeddedContext"] = true },
        },
        ["authMethods"] = new JsonArray(),
        ["agentInfo"] = new JsonObject { ["name"] = "nb", ["version"] = NbVersion.Current.Split('+')[0] },
    };

    private async Task NewSessionAsync(JsonNode id, JsonObject @params)
    {
        if (_session is not null)
        {
            SendError(id, InvalidRequest, "nb serves one session per process, and this one already has it.");
            return;
        }

        if (@params["cwd"] is JsonValue c && c.TryGetValue<string>(out var cwd) && cwd.Length > 0)
        {
            if (!Directory.Exists(cwd))
            {
                SendError(id, InvalidParams, $"cwd does not exist: {cwd}");
                return;
            }
            Directory.SetCurrentDirectory(cwd);
        }
        if (@params["mcpServers"] is JsonArray { Count: > 0 } servers)
            _log.WriteLine($"acp: ignoring {servers.Count} client-supplied MCP server(s); nb's come from its own mcp.json.");

        try
        {
            var session = await NbSession.StartAsync(_config, _options);
            try { await session.EvaluateAsync(_program); }
            catch { session.Dispose(); throw; }
            _session = session;
        }
        catch (Exception ex) when (ex is NbStartupException or TranscriptFormatException or SandboxUnavailableException
                                       or McpServerUnavailableException or ProviderUnavailableException)
        {
            SendError(id, InternalError, ex.Message);
            return;
        }

        ShowWarnings();
        _session.Journal.Written += Stream;
        _sessionId = $"nb-{Guid.NewGuid():N}";
        SendResult(id, new JsonObject { ["sessionId"] = _sessionId });
    }

    private void StartPrompt(JsonNode id, JsonObject @params)
    {
        if (_session is null || !SessionMatches(@params))
        {
            SendError(id, InvalidParams, "unknown sessionId; call session/new first.");
            return;
        }
        if (_prompt is { IsCompleted: false })
        {
            SendError(id, InvalidRequest, "a prompt is already running in this session.");
            return;
        }
        if (ToMessage(@params["prompt"] as JsonArray) is not { } prompt)
        {
            SendError(id, InvalidParams, "the prompt has no content nb can read (text, image, resource or resource_link).");
            return;
        }

        _promptCts?.Dispose();
        _promptCts = new CancellationTokenSource();
        var token = _promptCts.Token;
        _prompt = Task.Run(() => PromptAsync(id, prompt, token));
    }

    private async Task PromptAsync(JsonNode id, ChatMessage prompt, CancellationToken token)
    {
        var session = _session!;
        var before = session.Result().Usage;
        try
        {
            await session.PromptAsync(prompt, token);
        }
        catch (Exception ex) when (ex is TranscriptFormatException or McpServerUnavailableException or ProviderUnavailableException)
        {
            SendError(id, InternalError, ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _log.WriteLine($"acp: the prompt failed: {ex}");
            SendError(id, InternalError, ex.Message);
            return;
        }

        var result = session.Result();
        ShowWarnings(result.Warnings);
        // Before the response, so a client that sees the prompt end finds the file.
        if (_trajectoryPath is not null) WriteTrajectory(result);

        var reason = result.ExitReason;
        var meta = new JsonObject { ["nb"] = new JsonObject { ["exit_reason"] = reason } };
        if (StopReason(reason) is not { } stop)
        {
            SendError(id, InternalError, $"the run ended {reason}.", new JsonObject { ["exit_reason"] = reason });
            return;
        }
        var response = new JsonObject { ["stopReason"] = stop };
        if (Delta(before, result.Usage) is { } usage) response["usage"] = usage;
        response["_meta"] = meta;
        SendResult(id, response);
    }

    /// <summary>
    /// An exit reason as ACP's stopReason, or null for a run that failed rather than stopped
    /// (answered with a JSON-RPC error). Reasons ACP has no word for end the turn, and every
    /// response carries nb's own in <c>_meta.nb.exit_reason</c>.
    /// </summary>
    public static string? StopReason(string exitReason) => exitReason switch
    {
        ExitReasons.ProviderError or ExitReasons.RateLimited => null,
        ExitReasons.MaxToolCalls or ExitReasons.ToolErrorLimit or ExitReasons.OracleBudget => "max_turn_requests",
        ExitReasons.TokenBudget => "max_tokens",
        ExitReasons.Cancelled => "cancelled",
        _ => "end_turn",
    };

    // This prompt's tokens, in the shape ACP's (unstable) PromptResponse.usage takes. It is
    // also what Harbor's bridge scrapes from the session export as the target's usage.
    private static JsonObject? Delta(UsageInfo? before, UsageInfo? after)
    {
        if (after is null) return null;
        long Diff(long? a, long? b) => (a ?? 0) - (b ?? 0);
        return new JsonObject
        {
            ["inputTokens"] = Diff(after.Input, before?.Input),
            ["outputTokens"] = Diff(after.Output, before?.Output),
            ["totalTokens"] = Diff(after.Total, before?.Total),
        };
    }

    private void WriteTrajectory(RunResult result)
    {
        // Rewritten whole, through a rename, so a reader never sees half a file.
        var temp = _trajectoryPath + ".tmp";
        File.WriteAllText(temp, AtifWriter.Serialize(result.Trajectory()));
        File.Move(temp, _trajectoryPath!, overwrite: true);
    }

    // ---- The journal, live, as session/update.

    private void Stream(JournalEntry entry)
    {
        JsonObject? update = entry.Event switch
        {
            // The client's own prompt is not echoed back; a user turn nb wrote (the oracle's
            // answer, the loop or todo reminder) is news to it.
            UserEvent { Source: not null } u => Chunk("user_message_chunk", Flatten(u)),
            AssistantTextEvent a => Chunk("agent_message_chunk", a.Text ?? ""),
            ThinkingEvent t => Chunk("agent_thought_chunk", t.Text),
            ToolCallEvent { Approved: null } call => new JsonObject
            {
                ["sessionUpdate"] = "tool_call",
                ["toolCallId"] = call.Id,
                ["title"] = call.Name,
                ["kind"] = Kind(call.Name),
                ["status"] = "pending",
                ["rawInput"] = call.Arguments?.DeepClone() ?? new JsonObject(),
            },
            ToolCallEvent { Approved: ApprovalLedger.Allow } call => new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = call.Id,
                ["status"] = "in_progress",
            },
            ToolResultEvent r => new JsonObject
            {
                ["sessionUpdate"] = "tool_call_update",
                ["toolCallId"] = r.Id,
                ["status"] = _deniedCalls.Remove(r.Id) || r.Output.StartsWith("Error") ? "failed" : "completed",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "content",
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = r.Output },
                }),
            },
            _ => null,
        };
        // A denied call gets no update of its own: its result is the refusal, sent as failed.
        if (entry.Event is ToolCallEvent { Approved: ApprovalLedger.Deny } denied) _deniedCalls.Add(denied.Id);
        if (update is null) return;

        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = _sessionId, ["update"] = update },
        });
    }

    private static JsonObject Chunk(string kind, string text) => new()
    {
        ["sessionUpdate"] = kind,
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    };

    private static string Flatten(UserEvent u) =>
        u.Text ?? string.Join("\n", (u.Content ?? []).Select(p => p switch { TextPart t => t.Text, ImagePart i => i.Note ?? "[image]", _ => "" }));

    // ACP's tool kinds, for a client's icons. Proctor holds its own map for grading.
    private static string Kind(string tool) => tool switch
    {
        "bash" => "execute",
        "read_file" or "list_dir" => "read",
        "write_file" or "edit_file" or "apply_patch" => "edit",
        "find_files" or "grep" => "search",
        "fetch_url" or "search_web" => "fetch",
        "todo_write" or "todo_read" => "think",
        _ => "other",
    };

    // ---- An ACP prompt as one user message.

    /// <summary>
    /// The prompt's content blocks as one user message: text as text, an image as image
    /// data, an embedded text resource as its text, and a resource link as a line naming it
    /// (nb reads files itself, with its tools). Null when nothing in it is readable.
    /// </summary>
    public static ChatMessage? ToMessage(JsonArray? blocks)
    {
        var contents = new List<AIContent>();
        foreach (var block in (blocks ?? []).OfType<JsonObject>())
        {
            switch (Str(block, "type"))
            {
                case "text" when Str(block, "text") is { } text:
                    contents.Add(new TextContent(text));
                    break;
                case "image" when Str(block, "data") is { } data && Str(block, "mimeType") is { } mime:
                    try { contents.Add(new DataContent(Convert.FromBase64String(data), mime)); }
                    catch (FormatException) { return null; }
                    break;
                case "resource" when block["resource"] is JsonObject r && Str(r, "text") is { } body:
                    contents.Add(new TextContent($"<resource uri=\"{Str(r, "uri")}\">\n{body}\n</resource>"));
                    break;
                case "resource_link" when Str(block, "uri") is { } uri:
                    contents.Add(new TextContent($"[resource: {Str(block, "name") ?? uri}]({uri})"));
                    break;
            }
        }
        return contents.Count == 0 ? null : new ChatMessage(ChatRole.User, contents);
    }

    // ---- Writing.

    private void ShowWarnings(IReadOnlyList<string>? warnings = null)
    {
        // By text, not count: Result() appends its usage note after the session's own list.
        foreach (var w in warnings ?? _session?.Result().Warnings ?? [])
            if (_warningsShown.Add(w)) _log.WriteLine($"program: {w}");
    }

    private bool SessionMatches(JsonObject @params) =>
        _sessionId is not null && Str(@params, "sessionId") == _sessionId;

    private void SendResult(JsonNode id, JsonNode result) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

    private void SendError(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var error = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) error["data"] = data;
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = error });
    }

    // One line per message, whole, from whichever thread: the prompt's updates and the read
    // loop's answers interleave only between lines.
    private void Send(JsonObject message)
    {
        lock (_writeLock)
        {
            _out.Write(message.ToJsonString());
            _out.Write('\n');
            _out.Flush();
        }
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
