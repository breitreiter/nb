using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

public class MockProvider : IChatClientProvider
{
    public string Name => "Mock";
    public string[] RequiredConfigKeys => Array.Empty<string>();
    public bool CanCreate(IConfiguration config) => true;

    public IChatClient CreateClient(IConfiguration config)
    {
        var response = config["Response"] ?? "OK";
        var model = config["Model"];
        return new MockChatClient(response, model);
    }
}

/// <summary>
/// Mock chat client for testing. Supports MOCK:response=text in user messages
/// to control the response.
/// </summary>
public class MockChatClient : IChatClient
{
    // Fixed token usage reported per model round-trip, so tests can assert the
    // trailer's aggregate (e.g. a two-run program should report 2x these).
    public const int UsageInput = 10;
    public const int UsageOutput = 5;
    public const int UsageTotal = 15;

    // Mirrors nb.Transcript.OracleProtocol. Duplicated across the ALC boundary by
    // necessity, not by preference — see the comment on that class.
    public const string OracleSentinel = "[nb:oracle-resolver:1]";
    public const string OracleSubjectMarker = "[nb:oracle-resolver:1:subject]";
    public const string OracleRider = "MOCK:oracle=";
    public const string OracleDone = "DONE";

    private readonly string _defaultResponse;
    private readonly string? _model;
    private int _rateLimitHits;

    public MockChatClient(string defaultResponse = "OK", string? model = null)
    {
        _defaultResponse = defaultResponse;
        _model = model;
    }

    public ChatClientMetadata Metadata => new("MockProvider", new Uri("mock://localhost"), "mock-model");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Small delay to simulate real call
        await Task.Delay(10, cancellationToken);

        var lastUserMessage = chatMessages
            .LastOrDefault(m => m.Role == ChatRole.User)?
            .Text ?? "";

        // An ORACLE side call, not a turn of the subject conversation. nb opens that
        // call's last user message with this sentinel (nb.Core's OracleProtocol.Sentinel
        // — duplicated here because the Mock loads in its own AssemblyLoadContext and
        // cannot reference nb.Core; keep the two in step). Checked before every other
        // MOCK: form, because it selects a *mode* rather than a scripted behaviour: the
        // oracle prompt is nb's own text, so none of the riders below would match it.
        //
        // Why a rider rather than the usual last-user-message dispatch: on an oracle
        // call the last user message is nb's oracle prompt, so every MOCK: directive the
        // program scripted is out of scope and the call would fall through to the default
        // response — which is not a parseable verdict. Instead the verdict travels as
        // "MOCK:oracle=<ids|DONE|MISS>" riding on the subject's own scripted reply, which
        // is precisely what the oracle is shown. So one program line scripts both halves:
        //
        //     run MOCK:response=Which environment should I deploy to? MOCK:oracle=deploy-target
        //
        // The rider is scanned for anywhere within the JUDGED text (unlike the StartsWith
        // forms below), because it arrives embedded in quoted prose rather than at the
        // head of a message. The judged text is what follows the LAST subject marker; the
        // answer sheet precedes it in the same prompt and may carry riders of its own
        // (a sheet body is the subject's next scripted turn when a test chains asks), so
        // a scan over the whole prompt would hit the sheet on every call and never DONE.
        // With no marker at all (an oracle-shaped probe), the whole prompt is scanned.
        if (lastUserMessage.StartsWith(OracleSentinel, StringComparison.Ordinal))
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, ScriptedOracleVerdict(chatMessages)))
            {
                // Measured usage, like every other Mock reply — a resolved run should
                // not flip to "estimated" only because its side call went unmetered.
                Usage = new UsageDetails { InputTokenCount = UsageInput, OutputTokenCount = UsageOutput, TotalTokenCount = UsageTotal },
            };

        // MOCK:throw simulates a mid-turn provider/model failure so the
        // exit-code contract's provider_error path (exit 2) is testable.
        if (lastUserMessage.StartsWith("MOCK:throw", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("mock provider failure");

        // MOCK:ratelimit simulates a throttling rejection, shaped like the gateway
        // rejections that have no usable HTTP status — only prose. Bare, it always
        // throws (retries get exhausted); MOCK:ratelimit=N throws N times and then
        // answers, so a successful retry is observable end-to-end.
        const string ratePrefix = "MOCK:ratelimit";
        if (lastUserMessage.StartsWith(ratePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var budget = lastUserMessage.Length > ratePrefix.Length && lastUserMessage[ratePrefix.Length] == '='
                && int.TryParse(lastUserMessage[(ratePrefix.Length + 1)..].Split(' ')[0], out var n) ? n : int.MaxValue;

            if (Interlocked.Increment(ref _rateLimitHits) <= budget)
                throw new InvalidOperationException(
                    "Wholesale rate limit exceeded for this gateway. Please reduce request rate or use BYOK.");

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "recovered"));
        }

        // MOCK:model echoes this client's configured model, so a mid-stream model
        // swap (which rebuilds the client) is observable end-to-end.
        if (lastUserMessage.StartsWith("MOCK:model", StringComparison.OrdinalIgnoreCase))
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _model ?? "(none)"));

        // MOCK:loop=<name> <arg> scripts an UNTERMINATING tool call: it re-emits the
        // same call every round (scanning ALL user turns, so an injected loop/todo
        // reminder can't derail it), so the doom-loop / token / tool-call rails are
        // the only things that stop it. The identical signature trips the detector.
        const string loopPrefix = "MOCK:loop=";
        var loopMsg = chatMessages.FirstOrDefault(m =>
            m.Role == ChatRole.User && (m.Text ?? "").StartsWith(loopPrefix, StringComparison.OrdinalIgnoreCase))?.Text;
        if (loopMsg != null)
        {
            var spec = loopMsg[loopPrefix.Length..];
            var parts = spec.Split(' ', 2);
            var call = new FunctionCallContent("mock-loop", parts[0], BuildToolArgs(parts[0], parts.Length > 1 ? parts[1] : ""));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
        }

        // MOCK:tool=<name> <arg> scripts a single tool call so approval/tool-loop
        // paths are testable. It fires once: as soon as a tool result is in
        // history, we fall through to a plain answer, so the turn terminates
        // after one round instead of re-emitting the call forever.
        const string toolPrefix = "MOCK:tool=";
        bool toolAlreadyRan = chatMessages.Any(m => m.Role == ChatRole.Tool);
        if (!toolAlreadyRan && lastUserMessage.StartsWith(toolPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var spec = lastUserMessage[toolPrefix.Length..];
            var parts = spec.Split(' ', 2);
            var name = parts[0];
            var arg = parts.Length > 1 ? parts[1] : "";
            var call = new FunctionCallContent("mock-call-1", name, BuildToolArgs(name, arg));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call }));
        }

        // Check for special mock instructions in the message
        var response = ParseMockInstruction(lastUserMessage) ?? _defaultResponse;

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Yield the full response as a single update, carrying ALL content
        // (text and any function calls) so scripted tool calls survive the
        // streaming path — not just response.Text.
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Messages[0].Contents);

        // MOCK:nousage / MOCK:partialusage reproduce what a proxy or gateway between nb
        // and the real provider does to the usage block — drops it entirely, or forwards
        // the parts without a total. Both are what the estimator fallback and the
        // total-from-parts derivation exist to survive.
        var lastUserMessage = chatMessages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
        if (lastUserMessage.StartsWith("MOCK:nousage", StringComparison.OrdinalIgnoreCase))
            yield break;

        // A second update carrying usage, so ToChatResponse() aggregates it into
        // response.Usage the way a real streaming provider reports token counts.
        var usage = lastUserMessage.StartsWith("MOCK:partialusage", StringComparison.OrdinalIgnoreCase)
            ? new UsageDetails { InputTokenCount = UsageInput, OutputTokenCount = UsageOutput }
            : new UsageDetails { InputTokenCount = UsageInput, OutputTokenCount = UsageOutput, TotalTokenCount = UsageTotal };
        yield return new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent> { new UsageContent(usage) });
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }

    // Pull the scripted oracle verdict out of whatever the oracle call was shown.
    // Absent a rider the verdict is DONE — the conservative default the design calls for
    // ("when in doubt, DONE"), so an unscripted program cannot accidentally continue.
    private static string ScriptedOracleVerdict(IEnumerable<ChatMessage> chatMessages)
    {
        foreach (var whole in chatMessages.Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t)))
        {
            var marker = whole!.LastIndexOf(OracleSubjectMarker, StringComparison.Ordinal);
            var text = marker < 0 ? whole : whole[(marker + OracleSubjectMarker.Length)..];
            var i = text.IndexOf(OracleRider, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;

            // The verdict runs to the first whitespace: a comma-separated id list, or
            // DONE / MISS. Trailing punctuation is not stripped — ids are authored in the
            // program, so a stray period is a fixture bug worth seeing rather than hiding.
            var rest = text[(i + OracleRider.Length)..];
            var end = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            var verdict = (end < 0 ? rest : rest[..end]).Trim();
            if (verdict.Length > 0) return verdict;
        }
        return OracleDone;
    }

    // Maps a scripted tool name + raw arg to the argument dictionary that tool
    // expects. Only the tools exercised by tests need entries.
    private static Dictionary<string, object?> BuildToolArgs(string name, string arg) =>
        name.ToLowerInvariant() switch
        {
            "bash" => new() { ["command"] = arg, ["description"] = "scripted by MockProvider" },
            "search_web" => new() { ["query"] = arg },
            "fetch_url" => new() { ["url"] = arg },
            // Content is fixed: these scripted calls exercise approval and the tool loop,
            // and no test so far has cared what got written — only whether it was allowed.
            "write_file" => new() { ["path"] = arg, ["content"] = "scripted by MockProvider\n" },
            // read_file keeps its name across the qwen-code costume but changes its
            // parameter spelling, and the mock cannot see which harness is active — so
            // send both; the unused one is ignored either way.
            "read_file" => new() { ["path"] = arg, ["file_path"] = arg },
            "list_dir" => new() { ["path"] = arg },

            // The qwen-code costume's spellings, so a scripted call exercises the
            // harness's inbound translation end to end (plans/harness-emulation.md).
            "run_shell_command" => new() { ["command"] = arg, ["is_background"] = false, ["timeout"] = 30000 },
            "list_directory" => new() { ["file_path"] = arg, ["path"] = arg },
            "glob" => new() { ["pattern"] = arg },
            "grep_search" => new() { ["pattern"] = arg, ["glob"] = "*", ["limit"] = 5 },

            _ => new() { ["input"] = arg },
        };

    private static string? ParseMockInstruction(string message)
    {
        // Support MOCK:response=<text> to specify exact response
        const string prefix = "MOCK:response=";
        if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return message[prefix.Length..];
        }
        return null;
    }
}
