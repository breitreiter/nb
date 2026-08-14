using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using nb.MCP;
using nb.Shell;
using nb.Shell.ApplyPatch;
using nb.Harness;
using nb.Utilities;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace nb;

public class ConversationManager
{
    private const int DEFAULT_MAX_TOOL_CALLS = 25;
    private const int DEFAULT_MAX_CONTEXT_TOKENS = 128000;
    private const double DEFAULT_COMPACTION_THRESHOLD = 0.75;
    private static readonly TimeSpan McpToolTimeout = TimeSpan.FromSeconds(60);

    private IChatClient _client;
    private readonly McpManager _mcpManager;
    private readonly FakeToolManager _fakeToolManager;
    private BashTool? _bashTool;
    private ReadFileTool? _readFileTool;
    private WriteFileTool? _writeFileTool;
    private EditFileTool? _editFileTool;
    private FindFilesTool? _findFilesTool;
    private GrepTool? _grepTool;
    private ListDirTool? _listDirTool;
    private FetchUrlTool? _fetchUrlTool;
    private SearchWebTool? _searchWebTool;
    private ApplyPatchTool? _applyPatchTool;
    private readonly bool _verbose;
    private readonly bool _trustMode;
    private readonly bool _debugStream;
    private int _maxToolCalls;
    private readonly double _compactionThreshold;
    private int _maxContextTokens;
    private readonly float? _temperature;
    private readonly float? _presencePenalty;

    private static readonly System.Text.RegularExpressions.Regex ThinkBlockRegex =
        new(@"<think>[\s\S]*?</think>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripThinkBlocks(string text) =>
        string.IsNullOrEmpty(text) ? text : ThinkBlockRegex.Replace(text, string.Empty).Trim();

    private static AIChatMessage StripThinkBlocksFromMessage(AIChatMessage message)
    {
        if (!message.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text)))
            return message;
        var newContents = message.Contents
            .Select<AIContent, AIContent>(c => c is TextContent tc ? new TextContent(StripThinkBlocks(tc.Text)) : c)
            .ToList();
        return new AIChatMessage(message.Role, newContents);
    }
    private readonly List<AIChatMessage> _conversationHistory = new();
    private int _toolCallCount = 0;
    // Token usage accumulated across the whole invocation — every run and every
    // tool-loop round-trip within it. Never reset per run, so a multi-run program's
    // trailer reports the aggregate, not just the last run.
    private long _sessionInputTokens;
    private long _sessionOutputTokens;
    private long _sessionTotalTokens;
    private bool _sessionHadUsage;
    // Set when any round-trip's counts came from the size estimator rather than the
    // provider. Sticky: a session that mixes reported and estimated rounds is reported
    // as estimated, since the aggregate is no longer a measurement.
    private bool _usageEstimated;
    // Session-cumulative token ceiling; null = unlimited. When crossed, the run
    // aborts (ExitReasons.TokenBudget). The `budget tokens` directive sets it.
    private long? _tokenBudget;
    // Session-cumulative wall-clock ceiling (ms); null = unlimited. The stopwatch
    // starts at the first run; when the deadline passes, the in-flight model call is
    // cancelled and the run aborts (ExitReasons.TimeBudget). The `budget wall_ms`
    // directive sets it.
    private long? _wallBudgetMs;
    private readonly System.Diagnostics.Stopwatch _sessionStopwatch = new();
    private DoomLoopDetector _doomLoopDetector = new();
    private bool _doomLoopEnabled = true;
    private readonly ToolErrorTracker _errorTracker = new();
    private NbHarness _harness;
    private HashSet<string>? _lastRemindedTodos = null;
    private string _currentProviderName = "";
    private Transcript.ToolSurface _toolSurface = Transcript.ToolSurface.All;

    /// <summary>
    /// The native tool vocabulary — the names a <c>tools</c> directive toggles.
    /// <c>todo</c> is a steering aid (task-tracking + a pending-todos nudge) that
    /// rides the surface like any other tool, so <c>tools -todo</c> / <c>tools none</c>
    /// strips it; disabling it also silences the nudge (no todos can be created).
    /// (MCP-resource tools are internal bookkeeping and stay off the list.)
    /// Kept in sync with <see cref="NbHarness.CreateTools"/>, which is now the single
    /// site that assembles the native surface.
    /// </summary>
    public static readonly IReadOnlyList<string> NativeToolNames = new[]
    {
        "bash", "read_file", "write_file", "edit_file",
        "find_files", "grep", "list_dir", "apply_patch", "fetch_url", "search_web", "todo",
    };

    public ConversationManager(
        IChatClient client,
        McpManager mcpManager,
        FakeToolManager fakeToolManager,
        NbHarness harness,
        ApprovalPolicy approvalPolicy,
        string providerName = "",
        bool verbose = false,
        bool trustMode = false,
        int maxToolCalls = DEFAULT_MAX_TOOL_CALLS,
        int maxContextTokens = DEFAULT_MAX_CONTEXT_TOKENS,
        double compactionThreshold = DEFAULT_COMPACTION_THRESHOLD,
        bool debugStream = false,
        float? temperature = null,
        float? presencePenalty = null,
        int doomLoopThreshold = DoomLoopDetector.DefaultThreshold,
        bool doomLoopEnabled = true,
        long? tokenBudget = null,
        long? wallClockBudgetMs = null)
    {
        _client = client;
        _mcpManager = mcpManager;
        _fakeToolManager = fakeToolManager;
        _harness = harness;
        _harness.Configure(approvalPolicy, trustMode, verbose);
        // The dispatch arms below work in concrete tool instances, so mirror them out
        // of the harness rather than reaching through it at every call site.
        _bashTool = harness.Bash;
        _readFileTool = harness.ReadFile;
        _writeFileTool = harness.WriteFile;
        _editFileTool = harness.EditFile;
        _findFilesTool = harness.FindFiles;
        _grepTool = harness.Grep;
        _listDirTool = harness.ListDir;
        _fetchUrlTool = harness.FetchUrl;
        _searchWebTool = harness.SearchWeb;
        _applyPatchTool = harness.ApplyPatch;
        _currentProviderName = providerName;
        _verbose = verbose;
        _trustMode = trustMode;
        _maxToolCalls = trustMode ? Math.Max(maxToolCalls, 50) : maxToolCalls;
        _maxContextTokens = maxContextTokens;
        _compactionThreshold = compactionThreshold;
        _debugStream = debugStream;
        _temperature = temperature;
        _presencePenalty = presencePenalty;
        _doomLoopEnabled = doomLoopEnabled;
        _doomLoopDetector = new DoomLoopDetector(Math.Max(2, doomLoopThreshold));
        _tokenBudget = tokenBudget is > 0 ? tokenBudget : null;
        _wallBudgetMs = wallClockBudgetMs is > 0 ? wallClockBudgetMs : null;
    }

    /// <summary>
    /// Set the doom-loop detector for subsequent runs (the <c>loop</c> directive).
    /// <paramref name="enabled"/> false silences it; a threshold below 2 is floored.
    /// </summary>
    public void SetDoomLoop(bool enabled, int threshold)
    {
        _doomLoopEnabled = enabled;
        if (enabled) _doomLoopDetector = new DoomLoopDetector(Math.Max(2, threshold));
    }

    /// <summary>Set the session-cumulative token ceiling (the <c>budget tokens</c> directive). Non-positive = unlimited.</summary>
    public void SetTokenBudget(long? budget) => _tokenBudget = budget is > 0 ? budget : null;

    /// <summary>Set the session-cumulative wall-clock ceiling in ms (the <c>budget wall_ms</c> directive). Non-positive = unlimited.</summary>
    public void SetWallBudget(long? ms) => _wallBudgetMs = ms is > 0 ? ms : null;

    /// <summary>Override the per-turn tool-call cap (the <c>budget tool_calls</c> directive). Wins over config and the trust-mode floor.</summary>
    public void SetMaxToolCalls(int max) => _maxToolCalls = Math.Max(0, max);

    public void SwitchProvider(IChatClient newClient, string providerName, int maxContextTokens = DEFAULT_MAX_CONTEXT_TOKENS)
    {
        _client = newClient;
        _currentProviderName = providerName;
        _maxContextTokens = maxContextTokens;
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓ Switched to provider: {providerName}[/]");
    }

    public string GetCurrentProvider() => _currentProviderName;

    /// <summary>
    /// Set the tool surface in effect for subsequent runs — the resolved effect of
    /// the <c>mcp</c>/<c>tools</c> directives. The evaluator pushes this before each
    /// run, exactly as a provider change flows through <see cref="SwitchProvider"/>.
    /// Default (<see cref="Transcript.ToolSurface.All"/>): all native tools, all
    /// connected MCP servers. See plans/tool-surface-directives.md.
    /// </summary>
    /// <summary>The resolved approval policy — the <c>approval</c> directive layers onto it.</summary>
    public ApprovalPolicy ApprovalPolicy => _harness.ApprovalPolicy;

    /// <summary>The harness whose surface runs currently advertise.</summary>
    public NbHarness Harness => _harness;

    /// <summary>
    /// Swap the harness whose surface subsequent runs advertise (the <c>harness</c>
    /// directive). The tool instances behind it do not change — a costume swaps what is
    /// advertised, never what is implemented.
    /// </summary>
    public void SetHarness(NbHarness harness)
    {
        harness.Configure(_harness.ApprovalPolicy, _trustMode, _verbose);
        _harness = harness;
        _bashTool = harness.Bash;
        _readFileTool = harness.ReadFile;
        _writeFileTool = harness.WriteFile;
        _editFileTool = harness.EditFile;
        _findFilesTool = harness.FindFiles;
        _grepTool = harness.Grep;
        _listDirTool = harness.ListDir;
        _fetchUrlTool = harness.FetchUrl;
        _searchWebTool = harness.SearchWeb;
        _applyPatchTool = harness.ApplyPatch;
    }

    public void SetToolSurface(Transcript.ToolSurface surface)
    {
        // A program that names a server (mcp +name) which failed to connect is asking
        // for tools that will never arrive — hard-fail rather than run toolless.
        _mcpManager.AssertServersAvailable(surface.McpServers);
        _toolSurface = surface;
    }

    /// <summary>The live conversation history — the emit source for transcript output.</summary>
    public IReadOnlyList<AIChatMessage> History => _conversationHistory;

    /// <summary>
    /// Append pre-built messages (a loaded seed transcript) as premise state.
    /// The messages are inserted as-is; seeded tool rounds do not replay through
    /// the tool executors, so guards like FileReadTracker stay untouched — the
    /// agent re-reads before editing, exactly as intended (transcript-schema.md).
    /// </summary>
    public void AppendHistory(IEnumerable<AIChatMessage> messages) => _conversationHistory.AddRange(messages);

    /// <summary>Summed token usage across the whole invocation (all runs, all tool-loop round-trips), or null if no run happened.</summary>
    public (long input, long output, long total)? TotalUsage =>
        _sessionHadUsage ? (_sessionInputTokens, _sessionOutputTokens, _sessionTotalTokens) : null;

    /// <summary>
    /// True when <see cref="TotalUsage"/> includes counts the provider never reported —
    /// nb estimated them from message size. Off by roughly ±30%, and blind to the
    /// provider's own overheads, so treat it as a guardrail, not billing data.
    /// </summary>
    public bool UsageIsEstimated => _usageEstimated;

    /// <summary>
    /// The <c>exit_reason</c> of the most recent turn (see <see cref="Transcript.ExitReasons"/>).
    /// Feeds the jsonl trailer and the process exit code. Defaults to
    /// <see cref="Transcript.ExitReasons.Ok"/> before any turn runs.
    /// </summary>
    public string LastOutcome { get; private set; } = Transcript.ExitReasons.Ok;

    public Task SendMessageAsync(string userMessage) => RunAsync(userMessage);

    // Build the token a run executes under: the caller's token linked with a
    // wall-clock deadline (remaining budget) when one is set. Returns null when there
    // is no wall budget (the caller token is used directly). Caller disposes.
    private CancellationTokenSource? LinkWallDeadline(CancellationToken callerToken)
    {
        if (_wallBudgetMs is not { } wall) return null;
        var remaining = wall - _sessionStopwatch.ElapsedMilliseconds;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, remaining)));
        return cts;
    }

    /// <summary>
    /// Execute one <c>run</c> directive: optionally append <paramref name="inlinePrompt"/>
    /// as a user turn, then invoke the model on the current history. The evaluator
    /// calls this with the run's inline prompt (or null for a bare run on state
    /// already built by preceding turn directives). Resets per-turn trackers and
    /// records <see cref="LastOutcome"/>.
    /// </summary>
    public async Task RunAsync(string? inlinePrompt, CancellationToken cancellationToken = default)
    {
        if (_client == null) return;

        // The wall-clock budget is cumulative from the first run.
        if (!_sessionStopwatch.IsRunning) _sessionStopwatch.Start();

        // Reset per-turn trackers (token usage accumulates across runs — see the
        // _session* fields — so it is deliberately not reset here).
        _toolCallCount = 0;
        _doomLoopDetector.Reset();
        _errorTracker.Reset();
        _lastRemindedTodos = null;

        if (inlinePrompt is not null)
            _conversationHistory.Add(new AIChatMessage(ChatRole.User, inlinePrompt));

        // Already over a session budget from earlier runs — don't spend another model call.
        if (_tokenBudget is { } budget && _sessionTotalTokens >= budget)
        {
            LastOutcome = Transcript.ExitReasons.TokenBudget;
            return;
        }
        if (_wallBudgetMs is { } wall && _sessionStopwatch.ElapsedMilliseconds >= wall)
        {
            LastOutcome = Transcript.ExitReasons.TimeBudget;
            return;
        }

        using var linkedCts = LinkWallDeadline(cancellationToken);
        var runToken = linkedCts?.Token ?? cancellationToken;
        try
        {
            LastOutcome = await SendMessageInternalAsync(cancellationToken: runToken);
        }
        catch (OperationCanceledException)
        {
            // A caller-initiated cancel propagates (the CancellationToken contract);
            // the wall-clock deadline instead ends the run cleanly as time_budget.
            if (cancellationToken.IsCancellationRequested) throw;
            var note = $"Stopped: the wall-clock budget ({_wallBudgetMs} ms) was reached.";
            _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, note));
            RenderMarkdown(note);
            LastOutcome = Transcript.ExitReasons.TimeBudget;
        }
    }

    // Returns the turn's exit_reason (see ExitReasons). The reason propagates up
    // through the tool-loop recursion so the outermost caller sees why the turn
    // ended, not just that it did.
    private async Task<string> SendMessageInternalAsync(string? injectedReminder = null, CancellationToken cancellationToken = default)
    {
        if (_client == null) return Transcript.ExitReasons.Ok;

        // Abort promptly between round-trips if the deadline (or caller) has fired.
        cancellationToken.ThrowIfCancellationRequested();

        // Compact history if approaching context limit
        if (EstimateTokenCount() > (int)(_maxContextTokens * _compactionThreshold))
        {
            await CompactHistoryAsync(cancellationToken);
        }

        try
        {
            var requestOptions = new ChatOptions()
            {
                MaxOutputTokens = 10000,
                Temperature = _temperature,
                PresencePenalty = _presencePenalty,
            };

            // Assemble the tool list. The _toolSurface (mcp/tools directive effect)
            // gates both surfaces: MCP is uncontrolled (all connected) unless a
            // directive names servers; the harness applies the native filter itself.
            // MCP is not part of a harness — it is user configuration, not costume.
            var mcpTools = (_toolSurface.McpServers is { } servers
                    ? _mcpManager.GetToolsForServers(servers)
                    : _mcpManager.GetTools())
                .ToList();
            if (mcpTools.Count > 0)
            {
                mcpTools.Add(ResourceTools.CreateListResourcesTool(_mcpManager));
                mcpTools.Add(ResourceTools.CreateReadResourceTool(_mcpManager));
            }

            mcpTools.AddRange(_harness.CreateTools(_toolSurface));

            var allTools = _fakeToolManager.IntegrateWithMcpTools(mcpTools);
            if (allTools.Count > 0)
            {
                requestOptions.Tools = new List<AITool>();
                foreach (var tool in allTools)
                {
                    requestOptions.Tools.Add(tool);
                }
            }


            // Microsoft.Extensions.AI handles token limits cleanly without experimental methods

            // Stream the response. Prose renders incrementally through MarkdownRenderer;
            // tool-call content accumulates silently and gets executed after the stream ends.
            // Spinner is shown only until the first update arrives (TTFB indicator).
            var renderer = new MarkdownRenderer();
            var updates = new List<ChatResponseUpdate>();
            var updateTrace = new List<Dictionary<string, object?>>();
            var stream = _client.GetStreamingResponseAsync(_conversationHistory, requestOptions, cancellationToken);
            var enumerator = stream.GetAsyncEnumerator();
            ChatResponse response;
            try
            {
                var hasMore = await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .SpinnerStyle(Style.Parse(UIColors.SpectreMuted))
                    .StartAsync("Thinking...", async _ => await enumerator.MoveNextAsync());

                while (hasMore)
                {
                    var update = enumerator.Current;
                    updates.Add(update);
                    updateTrace.Add(SnapshotUpdate(update, updateTrace.Count));
                    var text = update.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        renderer.Append(text);
                    }
                    hasMore = await enumerator.MoveNextAsync();
                }
            }
            finally
            {
                renderer.Finish();
                await enumerator.DisposeAsync();
            }

            response = updates.ToChatResponse();

            // Accumulate token usage across every model round-trip (the tool loop
            // recurses through SendMessageInternalAsync) and across every run.
            //
            // Microsoft.Extensions.AI passes the provider's usage block through verbatim
            // and synthesizes nothing, so what arrives here is whatever survived the wire.
            // Two gaps matter, both common behind a proxy or gateway that sits between nb
            // and the real provider API:
            //
            //   1. A total is missing but the parts are present -> derive it. Nothing
            //      billed by tokens reports parts without a sum being computable.
            //   2. Nothing is reported at all (the streamed usage chunk was dropped, or
            //      the server ignored stream_options.include_usage) -> estimate.
            //
            // Gap 2 used to fail silently and dangerously: a null read as zero left
            // `budget tokens` inert, so a program that asked for a ceiling ran unbounded.
            // Warn-and-guess beats both alternatives — ignoring usage abandons the budget
            // the program asked for, and hard-failing forces every caller behind a gateway
            // to write a carve-out. The guess is flagged all the way out to the trailer so
            // it is never passed off as a measurement.
            var (roundInput, roundOutput, roundTotal) = MeasureOrEstimateUsage(response, requestOptions.Tools);
            _sessionInputTokens += roundInput;
            _sessionOutputTokens += roundOutput;
            _sessionTotalTokens += roundTotal;
            _sessionHadUsage = true;

            // Handle tool calls if present - check if any message has tool calls
            var hasToolCalls = response.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent));

            // Diagnostic dump: capture suspicious turns (or every turn under --debug-stream)
            // for the GPT-5x early-end bug. See bugs/Streaming_GPT5_Early_Turn_End.md.
            await MaybeWriteTurnDumpAsync(updates, updateTrace, response, hasToolCalls, injectedReminder);
            if (hasToolCalls)
            {
                // Stop before doing more work if the session token budget is spent.
                // Checked here (the model wants to continue) so a completed final
                // answer that merely nudges over the line still returns.
                if (_tokenBudget is { } budget && _sessionTotalTokens >= budget)
                {
                    var counted = _usageEstimated ? "estimated tokens used" : "tokens used";
                    var budgetMessage = $"Stopped: the token budget ({budget}) was reached ({_sessionTotalTokens} {counted}).";
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, budgetMessage));
                    RenderMarkdown(budgetMessage);
                    return Transcript.ExitReasons.TokenBudget;
                }

                // Check if we've exceeded max tool calls
                if (_toolCallCount >= _maxToolCalls)
                {
                    var limitMessage = "I've reached the maximum number of tool calls for this message. Let me provide a response with the information I have.";
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, limitMessage));
                    RenderMarkdown(limitMessage);
                    return Transcript.ExitReasons.MaxToolCalls;
                }

                // Add assistant message with tool calls to history (think blocks stripped)
                _conversationHistory.AddRange(response.Messages.Select(StripThinkBlocksFromMessage));

                // Collect all tool results in a single list
                var allToolResults = new List<ToolOutcome>();

                // Execute tool calls
                foreach (var message in response.Messages)
                {
                    foreach (var wireCall in message.Contents.OfType<FunctionCallContent>())
                    {
                        try
                        {
                            // A tool the surface didn't advertise (filtered by an mcp/tools
                            // directive, or otherwise unknown) is refused here — the dispatch
                            // table below still wires every constructed tool regardless of the
                            // surface, so this membership gate is what makes filtering real.
                            // Matched against the WIRE name, which is what was advertised.
                            if (!(requestOptions.Tools?.Any(t => t.Name == wireCall.Name) ?? false))
                            {
                                var errorMsg = $"Error: Tool '{wireCall.Name}' not found";
                                allToolResults.Add(ToolOutcome.Fail(wireCall.CallId, errorMsg));
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                continue;
                            }

                            // Translate the call out of the harness's vocabulary and into nb's,
                            // so every dispatch arm below — and approval, TrustSandbox and the
                            // read-tracker downstream of them — works in canonical identities
                            // and never learns that costumes exist. Identity for nb's own
                            // surface. History already holds the model's untranslated call, so
                            // the transcript records what actually went on the wire.
                            var (canonicalName, canonicalArgs) = _harness.ToCanonical(wireCall.Name, wireCall.Arguments);
                            var functionCall = ReferenceEquals(canonicalArgs, wireCall.Arguments) && canonicalName == wireCall.Name
                                ? wireCall
                                : new FunctionCallContent(wireCall.CallId, canonicalName, canonicalArgs);

                            // Check if this is a native resource tool (always auto-approve, read-only)
                            if (functionCall.Name.StartsWith("nb_"))
                            {
                                // Handle native resource tools - no approval needed
                                var resourceTool = mcpTools.FirstOrDefault(t => t.Name == functionCall.Name);
                                if (resourceTool != null)
                                {
                                    var arguments = new AIFunctionArguments();
                                    if (functionCall.Arguments != null)
                                    {
                                        foreach (var kvp in functionCall.Arguments)
                                        {
                                            arguments[kvp.Key] = kvp.Value?.ToString();
                                        }
                                    }

                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• calling {functionCall.Name}[/]");
                                    try
                                    {
                                        var result = await resourceTool.InvokeAsync(arguments, cancellationToken).AsTask().WaitAsync(McpToolTimeout, cancellationToken);
                                        var resultString = result?.ToString() ?? string.Empty;
                                        allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                        _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                                    }
                                    catch (TimeoutException)
                                    {
                                        var errorMsg = $"Error: Tool '{functionCall.Name}' timed out after {McpToolTimeout.TotalSeconds}s";
                                        allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                    }
                                }
                                else
                                {
                                    var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                }
                            }
                            // Check if this is a bash tool (custom approval UX)
                            else if (functionCall.Name == "bash" && _bashTool != null)
                            {
                                // Read defensively: a model can omit an argument the schema
                                // calls required, and the raw indexer throws on a missing key
                                // rather than yielding null (every other arm below already
                                // guards this way). Surfaced by the qwen-code costume, where
                                // description is genuinely optional.
                                var args = functionCall.Arguments;
                                var description = args != null && args.TryGetValue("description", out var d) ? d?.ToString() ?? "" : "";
                                var command = args != null && args.TryGetValue("command", out var c) ? c?.ToString() ?? "" : "";
                                var result = await _harness.HandleBashToolCall(functionCall.CallId, command, description);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is read_file (auto in cwd sandbox, prompt outside)
                            else if (functionCall.Name == "read_file" && _readFileTool != null)
                            {
                                var path = functionCall.Arguments?["path"]?.ToString() ?? "";
                                int? readOffset = functionCall.Arguments?.ContainsKey("offset") == true && functionCall.Arguments["offset"] != null
                                    ? int.Parse(functionCall.Arguments["offset"]!.ToString()!)
                                    : null;
                                int? readLimit = functionCall.Arguments?.ContainsKey("limit") == true && functionCall.Arguments["limit"] != null
                                    ? int.Parse(functionCall.Arguments["limit"]!.ToString()!)
                                    : null;

                                // Sandbox check: auto-approve reads in cwd/temp, prompt outside
                                var fullReadPath = _readFileTool.ResolvePath(path);
                                if (!_harness.ApproveReadPath("Read", fullReadPath, _readFileTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected read_file. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• reading {Markup.Escape(path)}[/]");

                                var readResult = _readFileTool.ReadFile(path, readOffset, readLimit);

                                if (!readResult.Success)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(readResult.Error ?? "Unknown error")}[/]");
                                    var errorString = $"Error: {readResult.Error}";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorString));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, errorString);
                                }
                                else if (readResult.FileType == "image")
                                {
                                    _harness.Files.RecordRead(readResult.Path);
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → image ({readResult.ImageSizeBytes:N0} bytes)[/]");
                                    var imageBytes = Convert.FromBase64String(readResult.ImageBase64!);
                                    var imageContent = new DataContent(imageBytes, readResult.MimeType!);
                                    var textNote = new TextContent($"[Image loaded: {Path.GetFileName(path)} ({readResult.ImageSizeBytes:N0} bytes)]");
                                    // Return tool result with both text description and image data
                                    allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, new List<AIContent> { textNote, imageContent }));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, $"[image: {readResult.MimeType}]");
                                }
                                else
                                {
                                    _harness.Files.RecordRead(readResult.Path);
                                    var label = readResult.FileType == "pdf"
                                        ? $"{readResult.TotalLines} pages"
                                        : $"{readResult.LinesReturned} lines ({readResult.TotalLines} total)";
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {label}[/]");
                                    var resultString = readResult.Content ?? "";
                                    allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                                }
                            }
                            // Check if this is edit_file (custom approval UX)
                            else if (functionCall.Name == "edit_file" && _editFileTool != null)
                            {
                                var path = functionCall.Arguments?["path"]?.ToString() ?? "";
                                var oldString = functionCall.Arguments?["old_string"]?.ToString() ?? "";
                                var newString = functionCall.Arguments?["new_string"]?.ToString() ?? "";
                                var replaceAll = functionCall.Arguments?.ContainsKey("replace_all") == true
                                    && functionCall.Arguments["replace_all"]?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;
                                var result = _harness.HandleEditFileToolCall(functionCall.CallId, path, oldString, newString, replaceAll);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is find_files (auto in cwd sandbox, prompt outside)
                            else if (functionCall.Name == "find_files" && _findFilesTool != null)
                            {
                                var pattern = functionCall.Arguments?["pattern"]?.ToString() ?? "";
                                var findPath = functionCall.Arguments?.ContainsKey("path") == true ? functionCall.Arguments["path"]?.ToString() : null;
                                int? findMax = functionCall.Arguments?.ContainsKey("max_results") == true && functionCall.Arguments["max_results"] != null
                                    ? int.Parse(functionCall.Arguments["max_results"]!.ToString()!)
                                    : null;

                                var fullFindPath = _findFilesTool.ResolvePath(findPath);
                                if (!_harness.ApproveReadPath("Find", fullFindPath, _findFilesTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected find_files. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• find_files: {Markup.Escape(pattern)}[/]");

                                var findResult = _findFilesTool.FindFiles(pattern, findPath, findMax);
                                string resultString;
                                if (findResult.Success)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {findResult.Files.Length} files ({findResult.TotalMatches} total)[/]");
                                    resultString = findResult.Output ?? "";
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(findResult.Error ?? "Unknown error")}[/]");
                                    resultString = $"Error: {findResult.Error}";
                                }

                                allToolResults.Add(findResult.Success
                                    ? ToolOutcome.Ok(functionCall.CallId, resultString)
                                    : ToolOutcome.Fail(functionCall.CallId, resultString));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                            }
                            // Check if this is grep (auto in cwd sandbox, prompt outside)
                            else if (functionCall.Name == "grep" && _grepTool != null)
                            {
                                var grepPattern = functionCall.Arguments?["pattern"]?.ToString() ?? "";
                                var grepPath = functionCall.Arguments?.ContainsKey("path") == true ? functionCall.Arguments["path"]?.ToString() : null;
                                var filePatternArg = functionCall.Arguments?.ContainsKey("file_pattern") == true ? functionCall.Arguments["file_pattern"]?.ToString() : null;
                                bool? caseInsensitive = functionCall.Arguments?.ContainsKey("case_insensitive") == true && functionCall.Arguments["case_insensitive"] != null
                                    ? functionCall.Arguments["case_insensitive"]?.ToString()?.Equals("true", StringComparison.OrdinalIgnoreCase)
                                    : null;
                                int? grepMax = functionCall.Arguments?.ContainsKey("max_results") == true && functionCall.Arguments["max_results"] != null
                                    ? int.Parse(functionCall.Arguments["max_results"]!.ToString()!)
                                    : null;

                                var fullGrepPath = _grepTool.ResolvePath(grepPath);
                                if (!_harness.ApproveReadPath("Grep", fullGrepPath, _grepTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected grep. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• grep: {Markup.Escape(grepPattern)}{(filePatternArg != null ? $" ({Markup.Escape(filePatternArg)})" : "")}[/]");

                                var grepOutputMode = functionCall.Arguments?.ContainsKey("output_mode") == true ? functionCall.Arguments["output_mode"]?.ToString() : null;

                                var grepResult = _grepTool.Grep(grepPattern, grepPath, filePatternArg, caseInsensitive, grepMax, grepOutputMode);
                                string resultString;
                                if (grepResult.Success)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {grepResult.Matches.Length} matches ({grepResult.TotalMatches} total)[/]");
                                    resultString = grepResult.Output ?? "";
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(grepResult.Error ?? "Unknown error")}[/]");
                                    resultString = $"Error: {grepResult.Error}";
                                }

                                allToolResults.Add(grepResult.Success
                                    ? ToolOutcome.Ok(functionCall.CallId, resultString)
                                    : ToolOutcome.Fail(functionCall.CallId, resultString));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                            }
                            // Check if this is list_dir (auto in cwd sandbox, prompt outside)
                            else if (functionCall.Name == "list_dir" && _listDirTool != null)
                            {
                                var listPath = functionCall.Arguments?.ContainsKey("path") == true ? functionCall.Arguments["path"]?.ToString() : null;

                                var fullListPath = _listDirTool.ResolvePath(listPath);
                                if (!_harness.ApproveReadPath("List", fullListPath, _listDirTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected list_dir. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    _harness.LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• list_dir: {Markup.Escape(listPath ?? ".")}[/]");

                                var listResult = _listDirTool.ListDir(listPath);
                                string resultString;
                                if (listResult.Success)
                                {
                                    var entryCount = listResult.Output?.Split('\n').Length ?? 0;
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {entryCount} entries[/]");
                                    resultString = listResult.Output ?? "";
                                }
                                else
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(listResult.Error ?? "Unknown error")}[/]");
                                    resultString = $"Error: {listResult.Error}";
                                }

                                allToolResults.Add(listResult.Success
                                    ? ToolOutcome.Ok(functionCall.CallId, resultString)
                                    : ToolOutcome.Fail(functionCall.CallId, resultString));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                            }
                            // search_web: custom approval UX, like fetch_url. The call is
                            // recorded whether or not a backend is configured — capturing that
                            // the model wanted to search is the point (plans/web-search.md).
                            else if (functionCall.Name == "search_web" && _searchWebTool != null)
                            {
                                var query = functionCall.Arguments?["query"]?.ToString() ?? "";
                                var result = await _harness.HandleSearchWebToolCall(functionCall.CallId, query);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is fetch_url (custom approval UX — always prompts)
                            else if (functionCall.Name == "fetch_url" && _fetchUrlTool != null)
                            {
                                var url = functionCall.Arguments?["url"]?.ToString() ?? "";
                                var result = await _harness.HandleFetchUrlToolCall(functionCall.CallId, url);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is write_file (custom approval UX)
                            else if (functionCall.Name == "write_file" && _writeFileTool != null)
                            {
                                var path = functionCall.Arguments?["path"]?.ToString() ?? "";
                                var content = functionCall.Arguments?["content"]?.ToString() ?? "";
                                var result = await _harness.HandleWriteFileToolCall(functionCall.CallId, path, content);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is apply_patch (custom approval UX for multi-file patches)
                            else if (functionCall.Name == "apply_patch" && _applyPatchTool != null)
                            {
                                var input = functionCall.Arguments?["input"]?.ToString() ?? "";
                                var result = _harness.HandleApplyPatchToolCall(functionCall.CallId, input);
                                allToolResults.Add(result);
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // todo_write / todo_read — always auto-approve, no approval prompt
                            else if (functionCall.Name == "todo_write")
                            {
                                var changes = ParseTodoChanges(functionCall.Arguments);
                                var resultString = _harness.Todo.Write(changes);
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_write ({changes.Count} change(s))[/]");
                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                            }
                            else if (functionCall.Name == "todo_read")
                            {
                                var resultString = _harness.Todo.Read();
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_read[/]");
                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                            }
                            // Check if this is a fake tool (always auto-approve)
                            else if (_fakeToolManager.GetFakeTool(functionCall.Name) is {} fakeTool)
                            {
                                // Fake tools are always auto-approved. Arguments arrive flat:
                                // the emitted schema declares the parameters directly, so there
                                // is no longer a nested "parameters" object to unwrap (there was,
                                // while fake tools registered as IDictionary and advertised one
                                // opaque property — see FakeToolManager.BuildSchema).
                                var displayArgs = functionCall.Arguments;
                                var argumentsJson = JsonSerializer.Serialize(displayArgs, new JsonSerializerOptions { WriteIndented = false });

                                var expandedResponse = _fakeToolManager.ExpandMacros(fakeTool.Response, displayArgs);

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Fake tool invoked: {functionCall.Name}[/]");
                                if (!_verbose)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]   Parameters: {Markup.Escape(argumentsJson)}[/]");
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]   → {Markup.Escape(expandedResponse)}[/]");
                                }

                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, expandedResponse));
                                _harness.LogToolCall(functionCall.Name, functionCall.Arguments, expandedResponse);
                            }
                            else
                            {
                                // Handle MCP tools - check approval
                                var mcpTool = mcpTools.FirstOrDefault(t => t.Name == functionCall.Name);
                                if (mcpTool != null)
                                {
                                    // Check the approval policy (alwaysAllow / Approval.McpTools) for this tool
                                    var mcpDecision = _harness.ApprovalPolicy.DecideMcp(functionCall.Name);
                                    bool approved = mcpDecision == ApprovalDecision.Allow;

                                    if (!approved && (NbHarness.NonInteractive || mcpDecision == ApprovalDecision.Deny))
                                    {
                                        // No terminal to prompt at, or the policy default is deny: refuse without a prompt.
                                        Console.Error.WriteLine($"[nb] denied: MCP tool '{functionCall.Name}' needs approval, but it is not allow-listed and {(NbHarness.NonInteractive ? "stdin is not a TTY" : "the approval policy default is deny")}.");
                                    }
                                    else if (!approved)
                                    {
                                        // Show tool call details and request approval
                                        var argumentsJson = JsonSerializer.Serialize(functionCall.Arguments, new JsonSerializerOptions { WriteIndented = true });

                                        while (true)
                                        {
                                            AnsiConsole.MarkupLine($"[{UIColors.SpectreUserPrompt}]Allow tool call: {functionCall.Name}? (Y/n/?)[/]");
                                            var key = Console.ReadKey().KeyChar;

                                            if (key == 'n')
                                            {
                                                approved = false;
                                                break;
                                            }
                                            else if (key == '?' )
                                            {
                                                AnsiConsole.MarkupLine($"[dim]Arguments:[/]");
                                                AnsiConsole.MarkupLine($"[dim]{argumentsJson}[/]");
                                                approved = AnsiConsole.Confirm("Allow this call?", defaultValue: true);
                                                break;
                                            }
                                            else if (key == '\r' || key == 'y')
                                            {
                                                approved = true;
                                                break;
                                            }
                                        }
                                    }

                                    if (!approved)
                                    {
                                        var reason = NbHarness.NonInteractive
                                            ? "non-interactive session; approval policy denied"
                                            : AnsiConsole.Prompt(
                                                new TextPrompt<string>("Reason for rejection [dim](optional)[/]:")
                                                    .DefaultValue("User declined")
                                                    .AllowEmpty()
                                            );

                                        var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                                            ? "Error: User rejected this tool call. Permission denied. Do not retry this action."
                                            : $"Error: User rejected this tool call. Reason: {reason}. Please consider an alternative approach based on the user's feedback.";

                                        allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejectionMessage));

                                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Tool call rejected, notifying model[/]");
                                        _harness.LogToolCall(functionCall.Name, functionCall.Arguments, rejectionMessage);
                                        _toolCallCount++;
                                        continue; // Skip to next tool call
                                    }

                                    // Execute approved MCP tool
                                    var arguments = new AIFunctionArguments();
                                    if (functionCall.Arguments != null)
                                    {
                                        foreach (var kvp in functionCall.Arguments)
                                        {
                                            arguments[kvp.Key] = kvp.Value?.ToString();
                                        }
                                    }

                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• calling {functionCall.Name}[/]");
                                    try
                                    {
                                        var result = await mcpTool.InvokeAsync(arguments, cancellationToken).AsTask().WaitAsync(McpToolTimeout, cancellationToken);
                                        var resultString = result?.ToString() ?? string.Empty;
                                        allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                        _harness.LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                                    }
                                    catch (TimeoutException)
                                    {
                                        var errorMsg = $"Error: Tool '{functionCall.Name}' timed out after {McpToolTimeout.TotalSeconds}s";
                                        allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                    }
                                }
                                else
                                {
                                    var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                }
                            }

                            // Increment tool call counter
                            _toolCallCount++;
                        }
                        catch (Exception ex)
                        {
                            // Reported under the wire name: it is what the model called, and
                            // the canonical translation may not have happened yet.
                            var errorMsg = $"Error: {ex.Message}";
                            allToolResults.Add(ToolOutcome.Fail(wireCall.CallId, errorMsg));
                            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Tool error ({Markup.Escape(wireCall.Name)}): {Markup.Escape(ex.Message)}[/]");
                            _harness.LogToolCall(wireCall.Name, wireCall.Arguments, errorMsg);
                        }
                    }
                }

                // Record signatures + error counts, amend failed results with retry hint
                var functionCalls = response.Messages
                    .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
                    .ToList();
                for (int i = 0; i < Math.Min(functionCalls.Count, allToolResults.Count); i++)
                {
                    var call = functionCalls[i];
                    if (_doomLoopEnabled) _doomLoopDetector.Record(call.Name, SerializeArgs(call.Arguments));

                    var (frc, isError) = allToolResults[i];
                    _errorTracker.RecordResult(call.Name, isError);
                    if (isError)
                    {
                        var remaining = _errorTracker.RemainingAttempts(call.Name);
                        if (remaining > 0)
                        {
                            var resultText = frc.Result?.ToString() ?? "";
                            frc.Result = $"{resultText}\n\n[nb] {call.Name} has failed {_errorTracker.ErrorCount(call.Name)} time(s); {remaining} attempt(s) left before this turn is aborted. Analyze the root cause and try a different approach — do not retry the same call.";
                        }
                    }
                }

                // Add all tool results as a single message
                if (allToolResults.Count > 0)
                {
                    var toolContents = allToolResults.Select(o => (AIContent)o.Content).ToList();
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Tool, toolContents));
                }

                // Hard-abort the turn if any tool has hit its failure budget
                if (_errorTracker.LimitReached(out var offendingTool))
                {
                    var abortMsg = $"Tool '{offendingTool}' failed {_errorTracker.Limit} times in a row. Aborting this turn to prevent a runaway loop. Review the errors above and try a different approach, or ask the user for help.";
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, abortMsg));
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]⛔ {Markup.Escape(abortMsg)}[/]");
                    return Transcript.ExitReasons.ToolErrorLimit;
                }

                // Inject a reminder if the model is looping
                string? nextInjectedReminder = null;
                if (_doomLoopEnabled && _doomLoopDetector.DetectLoop() is int reps)
                {
                    var reminder = $"<system_reminder>You appear to be stuck in a repetitive loop ({reps} similar tool-call sequences at the tail of this turn). You are not making progress. Options: (1) reconsider your approach, (2) try a different tool or different arguments, (3) stop and ask the user for clarification.</system_reminder>";
                    _conversationHistory.Add(new AIChatMessage(ChatRole.User, reminder));
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Loop detected ({reps} reps); reminding model[/]");
                    _doomLoopDetector.Reset();
                    nextInjectedReminder = "loop";
                }

                // Get another response after tool execution
                return await SendMessageInternalAsync(nextInjectedReminder, cancellationToken);
            }
            else
            {
                var assistantMessage = response.Text ?? string.Empty;

                if (!string.IsNullOrEmpty(assistantMessage))
                {
                    // Text was already streamed through MarkdownRenderer above.
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, StripThinkBlocks(assistantMessage)));
                }

                // Pending-todos reminder: if the model tries to end the turn with
                // unfinished todos, inject a reminder and continue. Only fires when
                // the active set has changed since the last reminder, so we don't
                // badger the model about the same list twice in a row.
                var activeTodos = _harness.Todos.GetActive();
                if (activeTodos.Count > 0)
                {
                    var currentSet = new HashSet<string>(activeTodos.Select(t => t.Content));
                    if (_lastRemindedTodos == null || !_lastRemindedTodos.SetEquals(currentSet))
                    {
                        var list = string.Join("\n", activeTodos.Select(t => $"- [{TodoManager.StatusLabel(t.Status)}] {t.Content}"));
                        var reminder = "<system_reminder>You have pending todo items that must be completed or cancelled before finishing this turn:\n"
                                     + list
                                     + "\n\nContinue working through the list, or mark items as cancelled via todo_write if they are no longer relevant.</system_reminder>";
                        _conversationHistory.Add(new AIChatMessage(ChatRole.User, reminder));
                        _lastRemindedTodos = currentSet;
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Pending todos; reminding model[/]");
                        return await SendMessageInternalAsync("todos", cancellationToken);
                    }
                }
            }

            return Transcript.ExitReasons.Ok;
        }
        catch (OperationCanceledException)
        {
            // Deadline / caller cancel — let RunAsync classify it (time_budget vs rethrow),
            // don't bury it as a provider error.
            throw;
        }
        catch (Exception ex)
        {
            // A throttling rejection that survived RetryingChatClient's backoff gets
            // its own exit_reason: the run is re-runnable, unlike a real model error.
            if (RateLimitClassifier.IsRateLimit(ex, out _))
            {
                AnsiConsole.MarkupLine(
                    $"[{UIColors.SpectreError}]Rate limited; retries exhausted: {Markup.Escape(ex.Message)}[/]");
                return Transcript.ExitReasons.RateLimited;
            }

            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: {Markup.Escape(ex.Message)}[/]");
            return Transcript.ExitReasons.ProviderError;
        }
    }



    private static void RenderMarkdown(string markdown) =>
        MarkdownRenderer.Render(markdown);

    private static Dictionary<string, object?> SnapshotUpdate(ChatResponseUpdate update, int index)
    {
        var contentTypes = new List<string>();
        var functionCalls = new List<Dictionary<string, object?>>();
        int textChars = 0;
        if (update.Contents != null)
        {
            foreach (var c in update.Contents)
            {
                contentTypes.Add(c.GetType().Name);
                switch (c)
                {
                    case TextContent tc:
                        textChars += tc.Text?.Length ?? 0;
                        break;
                    case FunctionCallContent fcc:
                        functionCalls.Add(new Dictionary<string, object?>
                        {
                            ["name"] = fcc.Name,
                            ["callId"] = fcc.CallId,
                            ["argChars"] = fcc.Arguments == null ? 0 : JsonSerializer.Serialize(fcc.Arguments).Length,
                        });
                        break;
                }
            }
        }
        return new Dictionary<string, object?>
        {
            ["index"] = index,
            ["finishReason"] = update.FinishReason?.ToString(),
            ["role"] = update.Role?.ToString(),
            ["responseId"] = update.ResponseId,
            ["messageId"] = update.MessageId,
            ["contentTypes"] = contentTypes,
            ["textChars"] = textChars,
            ["functionCalls"] = functionCalls,
        };
    }

    private async Task MaybeWriteTurnDumpAsync(
        List<ChatResponseUpdate> updates,
        List<Dictionary<string, object?>> updateTrace,
        ChatResponse response,
        bool hasToolCalls,
        string? injectedReminder)
    {
        // Counts from raw stream
        int rawFcc = 0, rawText = 0, rawUsage = 0;
        foreach (var u in updates)
        {
            if (u.Contents == null) continue;
            foreach (var c in u.Contents)
            {
                switch (c)
                {
                    case FunctionCallContent: rawFcc++; break;
                    case TextContent tc: rawText += tc.Text?.Length ?? 0; break;
                    case UsageContent: rawUsage++; break;
                }
            }
        }

        // Counts from aggregated response
        int aggFcc = 0, aggText = 0;
        foreach (var m in response.Messages)
        {
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case FunctionCallContent: aggFcc++; break;
                    case TextContent tc: aggText += tc.Text?.Length ?? 0; break;
                }
            }
        }

        // "Suspicious" signatures of the GPT-5x early-end bug:
        //  - empty turn: no tool calls AND zero text (a genuine "I give up")
        //  - reminder no-op: a recursive call after we injected a system_reminder produced
        //    no tool calls (matches "needs multiple `continue`s" report)
        //  - aggregation drop: raw stream had FunctionCallContent that ToChatResponse() lost
        bool emptyTurn = !hasToolCalls && aggText == 0;
        bool reminderNoOp = injectedReminder != null && !hasToolCalls;
        bool aggregationDrop = rawFcc > aggFcc;
        if (!_debugStream && !emptyTurn && !reminderNoOp && !aggregationDrop) return;
        string reason = aggregationDrop ? "aggregation_drop"
                      : reminderNoOp ? $"reminder_noop_{injectedReminder}"
                      : emptyTurn ? "empty_turn"
                      : "debug_stream";

        var dump = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTime.UtcNow.ToString("o"),
            ["provider"] = _currentProviderName,
            ["injectedReminder"] = injectedReminder,
            ["reason"] = reason,
            ["rawCounts"] = new Dictionary<string, object?>
            {
                ["updates"] = updates.Count,
                ["functionCallContent"] = rawFcc,
                ["textChars"] = rawText,
                ["usageContent"] = rawUsage,
            },
            ["aggregated"] = new Dictionary<string, object?>
            {
                ["messages"] = response.Messages.Count,
                ["functionCallContent"] = aggFcc,
                ["textChars"] = aggText,
                ["finishReason"] = response.FinishReason?.ToString(),
                ["responseId"] = response.ResponseId,
                ["usage"] = response.Usage == null ? null : new Dictionary<string, object?>
                {
                    ["input"] = response.Usage.InputTokenCount,
                    ["output"] = response.Usage.OutputTokenCount,
                    ["total"] = response.Usage.TotalTokenCount,
                },
            },
            ["updateTrace"] = updateTrace,
            ["rawResponseRepresentation"] = TrySerializeRaw(response.RawRepresentation),
        };

        try
        {
            var dir = Path.Combine(Directory.GetCurrentDirectory(), ".nb_turn_dumps");
            Directory.CreateDirectory(dir);
            var filename = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}_{(string)dump["reason"]!}.json";
            var path = Path.Combine(dir, filename);
            var json = JsonSerializer.Serialize(dump, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            await File.WriteAllTextAsync(path, json);
            var rel = Path.Combine(".nb_turn_dumps", filename);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ turn dump ({(string)dump["reason"]!}): {Markup.Escape(rel)}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]turn dump failed: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static object? TrySerializeRaw(object? raw)
    {
        if (raw == null) return null;
        try
        {
            // Round-trip through JsonSerializer with a depth cap; provider raw types
            // (OpenAI/Azure SDK responses) usually serialize cleanly but may carry
            // non-JSON-friendly fields, so fall back to ToString on failure.
            return JsonSerializer.SerializeToElement(raw, new JsonSerializerOptions
            {
                MaxDepth = 12,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            });
        }
        catch
        {
            return raw.ToString();
        }
    }

    private static string SerializeArgs(IDictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return "{}";
        try
        {
            return JsonSerializer.Serialize(args);
        }
        catch
        {
            return string.Join(",", args.Select(kv => $"{kv.Key}={kv.Value}"));
        }
    }

    private static List<TodoChange> ParseTodoChanges(IDictionary<string, object?>? args)
    {
        var changes = new List<TodoChange>();
        if (args == null || !args.TryGetValue("changes", out var raw) || raw == null)
            return changes;

        try
        {
            var json = raw is JsonElement je ? je.GetRawText() : raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return changes;

            var parsed = JsonSerializer.Deserialize<List<TodoChange>>(json!, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed != null) changes.AddRange(parsed);
        }
        catch
        {
            // Leave changes empty; todo_write will report "no changes submitted"
        }
        return changes;
    }

    // No interactive terminal to prompt at: approval must be resolved by policy,
    // not a key press. Anything already cleared by --approve/--trust is handled
    // before these prompts, so the Phase 0 policy here is a flat deny — reported
    // to the model as a structured tool error it can route around, never a hang
    // or a thrown ReadKey. (A richer per-tool policy arrives with Phase 5.)
    /// </summary>
    private (long Input, long Output, long Total) MeasureOrEstimateUsage(ChatResponse response, IList<AITool>? tools)
    {
        if (response.Usage is { } usage &&
            (usage.InputTokenCount ?? usage.OutputTokenCount ?? usage.TotalTokenCount) is not null)
        {
            long input = usage.InputTokenCount ?? 0;
            long output = usage.OutputTokenCount ?? 0;
            // Anthropic has no total_tokens field at all; a normalizing gateway may drop it.
            return (input, output, usage.TotalTokenCount ?? input + output);
        }

        // Called before the response is appended to history, so _conversationHistory is
        // still exactly the request that was sent — the input side, verbatim. Counting it
        // fresh each round mirrors how providers bill a tool loop (the whole prefix is
        // re-sent and re-charged every round-trip).
        long estInput = EstimateTokens(_conversationHistory) + EstimateToolSchemaTokens(tools);
        long estOutput = EstimateTokens(response.Messages);
        NoteUsageEstimated();
        return (estInput, estOutput, estInput + estOutput);
    }

    private void NoteUsageEstimated()
    {
        if (_usageEstimated) return;
        _usageEstimated = true;
        AnsiConsole.MarkupLine(
            $"[{UIColors.SpectreWarning}]warning: the provider reported no token usage; counts are estimated from message size " +
            "and any token budget is enforced against the estimate[/]");
    }

    private int EstimateTokenCount() => EstimateTokens(_conversationHistory);

    // Rough char-count heuristic (~3.5 chars/token). Drives compaction, and — when a
    // provider reports no usage — stands in for the wire counts. Deliberately crude:
    // it is a guardrail, not an accountant.
    private static int EstimateTokens(IEnumerable<AIChatMessage> messages)
    {
        long totalChars = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        totalChars += text.Text?.Length ?? 0;
                        break;
                    case FunctionCallContent fc:
                        totalChars += fc.Name?.Length ?? 0;
                        if (fc.Arguments != null)
                            totalChars += JsonSerializer.Serialize(fc.Arguments).Length;
                        break;
                    case FunctionResultContent fr:
                        totalChars += fr.Result?.ToString()?.Length ?? 0;
                        break;
                    case DataContent:
                        totalChars += 3500; // ~1000 tokens for images
                        break;
                }
            }
        }
        return (int)(totalChars / 3.5);
    }

    // Tool schemas are part of the billed input but live in ChatOptions, not history.
    // Only walked on the estimate path (a real usage report already includes them),
    // so the per-round serialization cost is paid rarely.
    private static int EstimateToolSchemaTokens(IList<AITool>? tools)
    {
        if (tools is null) return 0;
        long totalChars = 0;
        foreach (var tool in tools)
        {
            totalChars += tool.Name.Length + tool.Description.Length;
            if (tool is AIFunction fn) totalChars += fn.JsonSchema.GetRawText().Length;
        }
        return (int)(totalChars / 3.5);
    }

    private async Task CompactHistoryAsync(CancellationToken cancellationToken = default)
    {
        // Identify head: all leading system messages
        int headEnd = 0;
        while (headEnd < _conversationHistory.Count && _conversationHistory[headEnd].Role == ChatRole.System)
            headEnd++;

        // Identify tail: walk backward accumulating ~25% of context budget
        int tailBudget = (int)(_maxContextTokens * 0.25);
        int tailStart = _conversationHistory.Count;
        long tailChars = 0;
        while (tailStart > headEnd)
        {
            var msg = _conversationHistory[tailStart - 1];
            long msgChars = 0;
            foreach (var content in msg.Contents)
            {
                msgChars += content switch
                {
                    TextContent text => text.Text?.Length ?? 0,
                    FunctionCallContent fc => (fc.Name?.Length ?? 0) + (fc.Arguments != null ? JsonSerializer.Serialize(fc.Arguments).Length : 0),
                    FunctionResultContent fr => fr.Result?.ToString()?.Length ?? 0,
                    DataContent => 3500,
                    _ => 0
                };
            }

            if ((tailChars + msgChars) / 3.5 > tailBudget && tailStart < _conversationHistory.Count)
                break;

            tailChars += msgChars;
            tailStart--;
        }

        // Don't split tool-call/result pairs
        if (tailStart < _conversationHistory.Count && _conversationHistory[tailStart].Role == ChatRole.Tool && tailStart > headEnd)
            tailStart--; // include the preceding assistant message with FunctionCallContent
        if (tailStart < _conversationHistory.Count && _conversationHistory[tailStart].Role == ChatRole.Assistant
            && _conversationHistory[tailStart].Contents.Any(c => c is FunctionCallContent)
            && tailStart + 1 < _conversationHistory.Count && _conversationHistory[tailStart + 1].Role == ChatRole.Tool)
        {
            // Already good — assistant + tool pair starts at tailStart
        }

        // Compaction zone is [headEnd, tailStart)
        int zoneLength = tailStart - headEnd;
        if (zoneLength < 4)
            return; // not worth summarizing

        int beforeTokens = EstimateTokenCount();

        // Build text dump of compaction zone for summarization
        var sb = new System.Text.StringBuilder();
        for (int i = headEnd; i < tailStart; i++)
        {
            var msg = _conversationHistory[i];
            sb.AppendLine($"[{msg.Role}]");
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        sb.AppendLine(text.Text);
                        break;
                    case FunctionCallContent fc:
                        sb.AppendLine($"Tool call: {fc.Name}({JsonSerializer.Serialize(fc.Arguments)})");
                        break;
                    case FunctionResultContent fr:
                        var resultText = fr.Result?.ToString() ?? "";
                        // Truncate very large tool results in the summary input
                        if (resultText.Length > 2000)
                            resultText = resultText[..1000] + "\n...\n" + resultText[^500..];
                        sb.AppendLine($"Tool result: {resultText}");
                        break;
                }
            }
        }

        // Summarize via LLM
        string summary;
        try
        {
            var summarizeMessages = new List<AIChatMessage>
            {
                new(ChatRole.System, "Summarize the following conversation excerpt concisely. Preserve key facts, decisions, file paths, code changes made, and any instructions the user gave. Output only the summary."),
                new(ChatRole.User, sb.ToString())
            };
            var summarizeOptions = new ChatOptions { MaxOutputTokens = 2000 };
            var response = await _client.GetResponseAsync(summarizeMessages, summarizeOptions, cancellationToken);
            summary = response.Text ?? "";
        }
        catch
        {
            // Fallback: drop oldest half of compaction zone without summary
            int halfZone = zoneLength / 2;
            _conversationHistory.RemoveRange(headEnd, halfZone);
            int afterTokens = EstimateTokenCount();
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Trimmed conversation history ({beforeTokens:N0} → {afterTokens:N0} est. tokens)[/]");
            return;
        }

        // Replace compaction zone with summary
        _conversationHistory.RemoveRange(headEnd, zoneLength);
        _conversationHistory.Insert(headEnd, new AIChatMessage(ChatRole.Assistant, $"[Conversation summary]\n{summary}"));

        int afterTokensFinal = EstimateTokenCount();
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Compacted conversation ({beforeTokens:N0} → {afterTokensFinal:N0} est. tokens)[/]");
    }

    public void ClearConversationHistory()
    {
        // Preserve all leading system messages (base prompt + skill)
        var systemMessages = _conversationHistory.TakeWhile(m => m.Role == ChatRole.System).ToList();
        _conversationHistory.Clear();
        _conversationHistory.AddRange(systemMessages);
        _harness.Todos.Reset();
        _lastRemindedTodos = null;

        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Conversation history cleared[/]");
    }
}
