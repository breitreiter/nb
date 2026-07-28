using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Spectre.Console;
using nb.MCP;
using nb.Shell;
using nb.Shell.ApplyPatch;
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
    private readonly BashTool? _bashTool;
    private readonly ReadFileTool? _readFileTool;
    private readonly WriteFileTool? _writeFileTool;
    private readonly EditFileTool? _editFileTool;
    private readonly FindFilesTool? _findFilesTool;
    private readonly GrepTool? _grepTool;
    private readonly ListDirTool? _listDirTool;
    private readonly FetchUrlTool? _fetchUrlTool;
    private readonly ApplyPatchTool? _applyPatchTool;
    private readonly FileReadTracker _fileReadTracker = new();
    private readonly ApprovalPolicy _approvalPolicy;
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
    private readonly TodoManager _todoManager = new();
    private readonly TodoTool _todoTool;
    private HashSet<string>? _lastRemindedTodos = null;
    private string _currentProviderName = "";
    private Transcript.ToolSurface _toolSurface = Transcript.ToolSurface.All;

    /// <summary>
    /// The native tool vocabulary — the names a <c>tools</c> directive toggles.
    /// <c>todo</c> is a steering aid (task-tracking + a pending-todos nudge) that
    /// rides the surface like any other tool, so <c>tools -todo</c> / <c>tools none</c>
    /// strips it; disabling it also silences the nudge (no todos can be created).
    /// (MCP-resource tools are internal bookkeeping and stay off the list.)
    /// Kept in sync with the assembly in <see cref="SendMessageInternalAsync"/> and
    /// <see cref="GetAvailableTools"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> NativeToolNames = new[]
    {
        "bash", "read_file", "write_file", "edit_file",
        "find_files", "grep", "list_dir", "apply_patch", "fetch_url", "todo",
    };

    public ConversationManager(
        IChatClient client,
        McpManager mcpManager,
        FakeToolManager fakeToolManager,
        BashTool? bashTool,
        ReadFileTool? readFileTool,
        WriteFileTool? writeFileTool,
        EditFileTool? editFileTool,
        FindFilesTool? findFilesTool,
        GrepTool? grepTool,
        ListDirTool? listDirTool,
        FetchUrlTool? fetchUrlTool,
        ApplyPatchTool? applyPatchTool,
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
        _bashTool = bashTool;
        _readFileTool = readFileTool;
        _writeFileTool = writeFileTool;
        _editFileTool = editFileTool;
        _findFilesTool = findFilesTool;
        _grepTool = grepTool;
        _listDirTool = listDirTool;
        _fetchUrlTool = fetchUrlTool;
        _applyPatchTool = applyPatchTool;
        _approvalPolicy = approvalPolicy;
        _currentProviderName = providerName;
        _verbose = verbose;
        _trustMode = trustMode;
        _maxToolCalls = trustMode ? Math.Max(maxToolCalls, 50) : maxToolCalls;
        _maxContextTokens = maxContextTokens;
        _compactionThreshold = compactionThreshold;
        _debugStream = debugStream;
        _temperature = temperature;
        _presencePenalty = presencePenalty;
        _todoTool = new TodoTool(_todoManager);
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

    /// <summary>Summed token usage across the whole invocation (all runs, all tool-loop round-trips), or null if the provider reported none.</summary>
    public (long input, long output, long total)? TotalUsage =>
        _sessionHadUsage ? (_sessionInputTokens, _sessionOutputTokens, _sessionTotalTokens) : null;

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

            // Assemble the tool list. NOTE: GetAvailableTools() mirrors this for the
            // /tools command — keep the two in sync when adding/removing tools.
            // The _toolSurface (mcp/tools directive effect) gates both surfaces:
            // MCP is uncontrolled (all connected) unless a directive names servers;
            // native tools are all-on unless a directive filters them.
            var mcpTools = (_toolSurface.McpServers is { } servers
                    ? _mcpManager.GetToolsForServers(servers)
                    : _mcpManager.GetTools())
                .ToList();
            if (mcpTools.Count > 0)
            {
                mcpTools.Add(ResourceTools.CreateListResourcesTool(_mcpManager));
                mcpTools.Add(ResourceTools.CreateReadResourceTool(_mcpManager));
            }

            // Add native tools if wired (nullable via --nobash etc.) and allowed by
            // the surface (a tools directive may drop them).
            if (_bashTool != null && _toolSurface.AllowsNative("bash"))
            {
                mcpTools.Add(_bashTool.CreateTool());
            }

            if (_readFileTool != null && _toolSurface.AllowsNative("read_file"))
            {
                mcpTools.Add(_readFileTool.CreateTool());
            }

            if (_writeFileTool != null && _toolSurface.AllowsNative("write_file"))
            {
                mcpTools.Add(_writeFileTool.CreateTool());
            }

            if (_editFileTool != null && _toolSurface.AllowsNative("edit_file"))
            {
                mcpTools.Add(_editFileTool.CreateTool());
            }

            if (_findFilesTool != null && _toolSurface.AllowsNative("find_files"))
            {
                mcpTools.Add(_findFilesTool.CreateTool());
            }

            if (_grepTool != null && _toolSurface.AllowsNative("grep"))
            {
                mcpTools.Add(_grepTool.CreateTool());
            }

            if (_listDirTool != null && _toolSurface.AllowsNative("list_dir"))
            {
                mcpTools.Add(_listDirTool.CreateTool());
            }

            if (_applyPatchTool != null && _toolSurface.AllowsNative("apply_patch"))
            {
                mcpTools.Add(_applyPatchTool.CreateTool());
            }

            if (_fetchUrlTool != null && _toolSurface.AllowsNative("fetch_url"))
            {
                mcpTools.Add(_fetchUrlTool.CreateTool());
            }

            // todo rides the native surface: on by default, dropped by `tools -todo`
            // / `tools none`. Removing it also silences the pending-todos nudge, since
            // no todos can be created without the write tool.
            if (_toolSurface.AllowsNative("todo"))
            {
                mcpTools.Add(_todoTool.CreateWriteTool());
                mcpTools.Add(_todoTool.CreateReadTool());
            }

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
            if (response.Usage is { } usage)
            {
                _sessionInputTokens += usage.InputTokenCount ?? 0;
                _sessionOutputTokens += usage.OutputTokenCount ?? 0;
                _sessionTotalTokens += usage.TotalTokenCount ?? 0;
                _sessionHadUsage = true;
            }

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
                    var budgetMessage = $"Stopped: the token budget ({budget}) was reached ({_sessionTotalTokens} tokens used).";
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
                    foreach (var functionCall in message.Contents.OfType<FunctionCallContent>())
                    {
                        try
                        {
                            // A tool the surface didn't advertise (filtered by an mcp/tools
                            // directive, or otherwise unknown) is refused here — the dispatch
                            // table below still wires every constructed tool regardless of the
                            // surface, so this membership gate is what makes filtering real.
                            if (!(requestOptions.Tools?.Any(t => t.Name == functionCall.Name) ?? false))
                            {
                                var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{errorMsg}[/]");
                                continue;
                            }

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
                                        LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
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
                                var description = functionCall.Arguments?["description"]?.ToString() ?? "";
                                var command = functionCall.Arguments?["command"]?.ToString() ?? "";
                                var result = await HandleBashToolCall(functionCall.CallId, command, description);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
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
                                if (!ApproveReadPath("Read", fullReadPath, _readFileTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected read_file. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
                                    continue;
                                }

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• reading {Markup.Escape(path)}[/]");

                                var readResult = _readFileTool.ReadFile(path, readOffset, readLimit);

                                if (!readResult.Success)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(readResult.Error ?? "Unknown error")}[/]");
                                    var errorString = $"Error: {readResult.Error}";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorString));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, errorString);
                                }
                                else if (readResult.FileType == "image")
                                {
                                    _fileReadTracker.RecordRead(readResult.Path);
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → image ({readResult.ImageSizeBytes:N0} bytes)[/]");
                                    var imageBytes = Convert.FromBase64String(readResult.ImageBase64!);
                                    var imageContent = new DataContent(imageBytes, readResult.MimeType!);
                                    var textNote = new TextContent($"[Image loaded: {Path.GetFileName(path)} ({readResult.ImageSizeBytes:N0} bytes)]");
                                    // Return tool result with both text description and image data
                                    allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, new List<AIContent> { textNote, imageContent }));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, $"[image: {readResult.MimeType}]");
                                }
                                else
                                {
                                    _fileReadTracker.RecordRead(readResult.Path);
                                    var label = readResult.FileType == "pdf"
                                        ? $"{readResult.TotalLines} pages"
                                        : $"{readResult.LinesReturned} lines ({readResult.TotalLines} total)";
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {label}[/]");
                                    var resultString = readResult.Content ?? "";
                                    allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
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
                                var result = HandleEditFileToolCall(functionCall.CallId, path, oldString, newString, replaceAll);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
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
                                if (!ApproveReadPath("Find", fullFindPath, _findFilesTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected find_files. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
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
                                LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
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
                                if (!ApproveReadPath("Grep", fullGrepPath, _grepTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected grep. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
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
                                LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                            }
                            // Check if this is list_dir (auto in cwd sandbox, prompt outside)
                            else if (functionCall.Name == "list_dir" && _listDirTool != null)
                            {
                                var listPath = functionCall.Arguments?.ContainsKey("path") == true ? functionCall.Arguments["path"]?.ToString() : null;

                                var fullListPath = _listDirTool.ResolvePath(listPath);
                                if (!ApproveReadPath("List", fullListPath, _listDirTool.GetCwd()))
                                {
                                    var rejMsg = "Error: User rejected list_dir. Path is outside working directory.";
                                    allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, rejMsg));
                                    LogToolCall(functionCall.Name, functionCall.Arguments, rejMsg);
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
                                LogToolCall(functionCall.Name, functionCall.Arguments, resultString.Length > 200 ? resultString[..200] + "..." : resultString);
                            }
                            // Check if this is fetch_url (custom approval UX — always prompts)
                            else if (functionCall.Name == "fetch_url" && _fetchUrlTool != null)
                            {
                                var url = functionCall.Arguments?["url"]?.ToString() ?? "";
                                var result = await HandleFetchUrlToolCall(functionCall.CallId, url);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is write_file (custom approval UX)
                            else if (functionCall.Name == "write_file" && _writeFileTool != null)
                            {
                                var path = functionCall.Arguments?["path"]?.ToString() ?? "";
                                var content = functionCall.Arguments?["content"]?.ToString() ?? "";
                                var result = await HandleWriteFileToolCall(functionCall.CallId, path, content);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // Check if this is apply_patch (custom approval UX for multi-file patches)
                            else if (functionCall.Name == "apply_patch" && _applyPatchTool != null)
                            {
                                var input = functionCall.Arguments?["input"]?.ToString() ?? "";
                                var result = HandleApplyPatchToolCall(functionCall.CallId, input);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Content.Result?.ToString() ?? "");
                            }
                            // todo_write / todo_read — always auto-approve, no approval prompt
                            else if (functionCall.Name == "todo_write")
                            {
                                var changes = ParseTodoChanges(functionCall.Arguments);
                                var resultString = _todoTool.Write(changes);
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_write ({changes.Count} change(s))[/]");
                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                            }
                            else if (functionCall.Name == "todo_read")
                            {
                                var resultString = _todoTool.Read();
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_read[/]");
                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, resultString));
                                LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                            }
                            // Check if this is a fake tool (always auto-approve)
                            else if (_fakeToolManager.GetFakeTool(functionCall.Name) is {} fakeTool)
                            {
                                // Handle fake tool - no approval needed
                                // Extract nested "parameters" if present (from IDictionary schema)
                                var displayArgs = functionCall.Arguments;
                                if (displayArgs?.Count == 1 &&
                                    displayArgs.TryGetValue("parameters", out var nested) &&
                                    nested is JsonElement nestedElement)
                                {
                                    displayArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(nestedElement.GetRawText());
                                }
                                var argumentsJson = JsonSerializer.Serialize(displayArgs, new JsonSerializerOptions { WriteIndented = false });

                                var expandedResponse = _fakeToolManager.ExpandMacros(fakeTool.Response, displayArgs);

                                AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Fake tool invoked: {functionCall.Name}[/]");
                                if (!_verbose)
                                {
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]   Parameters: {Markup.Escape(argumentsJson)}[/]");
                                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]   → {Markup.Escape(expandedResponse)}[/]");
                                }

                                allToolResults.Add(ToolOutcome.Ok(functionCall.CallId, expandedResponse));
                                LogToolCall(functionCall.Name, functionCall.Arguments, expandedResponse);
                            }
                            else
                            {
                                // Handle MCP tools - check approval
                                var mcpTool = mcpTools.FirstOrDefault(t => t.Name == functionCall.Name);
                                if (mcpTool != null)
                                {
                                    // Check the approval policy (alwaysAllow / Approval.McpTools) for this tool
                                    var mcpDecision = _approvalPolicy.DecideMcp(functionCall.Name);
                                    bool approved = mcpDecision == ApprovalDecision.Allow;

                                    if (!approved && (NonInteractive || mcpDecision == ApprovalDecision.Deny))
                                    {
                                        // No terminal to prompt at, or the policy default is deny: refuse without a prompt.
                                        Console.Error.WriteLine($"[nb] denied: MCP tool '{functionCall.Name}' needs approval, but it is not allow-listed and {(NonInteractive ? "stdin is not a TTY" : "the approval policy default is deny")}.");
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
                                        var reason = NonInteractive
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
                                        LogToolCall(functionCall.Name, functionCall.Arguments, rejectionMessage);
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
                                        LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
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
                            var errorMsg = $"Error: {ex.Message}";
                            allToolResults.Add(ToolOutcome.Fail(functionCall.CallId, errorMsg));
                            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Tool error ({Markup.Escape(functionCall.Name)}): {Markup.Escape(ex.Message)}[/]");
                            LogToolCall(functionCall.Name, functionCall.Arguments, errorMsg);
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
                var activeTodos = _todoManager.GetActive();
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
    private static bool NonInteractive => Console.IsInputRedirected;

    // A dispatched tool call's outcome: the content returned to the model plus whether
    // it represents a failure. IsError is decided where the result is built — a tool's
    // Success flag, a denial, a timeout, a caught exception — so error accounting
    // (ToolErrorTracker) never has to sniff the result text for an "Error:" prefix.
    private readonly record struct ToolOutcome(FunctionResultContent Content, bool IsError)
    {
        public static ToolOutcome Ok(string callId, object? result) => new(new FunctionResultContent(callId, result), false);
        public static ToolOutcome Fail(string callId, string message) => new(new FunctionResultContent(callId, message), true);
    }

    private static ToolOutcome DenyNonInteractive(string callId, string what)
    {
        Console.Error.WriteLine($"[nb] denied: {what} needs approval, but stdin is not a TTY and nothing pre-approved it.");
        return ToolOutcome.Fail(callId,
            $"Error: {what} requires approval, but this is a non-interactive session (stdin is not a TTY) " +
            "and no pre-approval (--approve/--trust) matched. Permission denied — do not retry; try a different approach.");
    }

    // The approval policy's default is deny: this call matched no allow-rule, so it
    // is refused without a prompt (even interactively). Distinct from the non-TTY
    // deny — it's a deliberate lockdown, not a missing terminal.
    private static ToolOutcome DenyByPolicy(string callId, string what)
    {
        Console.Error.WriteLine($"[nb] denied: {what} — approval policy default is deny and nothing allow-listed it.");
        return ToolOutcome.Fail(callId,
            $"Error: {what} was denied by the approval policy (default: deny) and no allow-rule matched. " +
            "Permission denied — do not retry; try a different approach.");
    }

    /// <summary>The resolved approval policy — the <c>approval</c> directive layers onto it (Phase 5.2b).</summary>
    public ApprovalPolicy ApprovalPolicy => _approvalPolicy;

    private async Task<ToolOutcome> HandleBashToolCall(string callId, string command, string description)
    {
        try
        {
            // Classify the command for display
            var classified = CommandClassifier.Classify(command);

            // The approval policy owns the auto-approve precedence (--approve → safe
            // allowlist → trust+sandbox). Allow carries a reason for the log line.
            var cwd = _bashTool?.GetCwd() ?? "";
            var (decision, approveReason) = _approvalPolicy.DecideBash(command, classified, cwd, _bashTool != null);
            if (decision == ApprovalDecision.Allow)
            {
                var display = Markup.Escape(classified.DisplayText);
                var line = approveReason switch
                {
                    "pre-approved" => $"• bash (pre-approved): {display}",
                    "trust" => $"• auto: bash {display}",
                    _ => $"• bash: {display}",
                };
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{line}[/]");
                return await ExecuteBashCommand(callId, command);
            }

            // Policy default is deny: refuse without prompting (even interactively).
            if (decision == ApprovalDecision.Deny)
                return DenyByPolicy(callId, $"bash ({classified.Category})");

            // Show model's description of intent (if provided)
            if (!string.IsNullOrWhiteSpace(description))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]{Markup.Escape(description)}[/]");
            }

            // Show approval prompt with command classification
            var categoryColor = classified.IsDangerous ? UIColors.SpectreError : UIColors.SpectreWarning;
            var warningIndicator = classified.IsDangerous ? " ⚠️" : "";

            // For dangerous commands, show the full command so the user can see what they're approving
            var displayCommand = classified.IsDangerous ? command : classified.DisplayText;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{classified.Category}:[/] {Markup.Escape(displayCommand)}");

            if (classified.IsDangerous && classified.DangerReason != null)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: {classified.DangerReason}[/]");
            }

            if (NonInteractive)
                return DenyNonInteractive(callId, $"bash ({classified.Category})");

            // Default based on danger level
            var defaultYes = !classified.IsDangerous;
            var options = classified.IsDangerous ? "[[y/N/?]]" : "[[Y/n/?]]";

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? {options}[/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || (!defaultYes && (key == '\r' || key == '\n')))
                {
                    // Rejected
                    var reason = AnsiConsole.Prompt(
                        new TextPrompt<string>($"[{UIColors.SpectreMuted}]Reason (optional):[/]")
                            .DefaultValue("User declined")
                            .AllowEmpty()
                    );

                    var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                        ? "Error: User rejected this command. Permission denied."
                        : $"Error: User rejected this command. Reason: {reason}";

                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Command rejected[/]");
                    return ToolOutcome.Fail(callId, rejectionMessage);
                }
                else if (key == '?')
                {
                    // Show full command
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Full command:[/]");
                    AnsiConsole.WriteLine(command);
                    continue;
                }
                else if (key == 'y' || key == 'Y' || (defaultYes && (key == '\r' || key == '\n')))
                {
                    // Approved
                    return await ExecuteBashCommand(callId, command);
                }
                // For any other key, loop and ask again
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Approval error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during command approval: {ex.Message}");
        }
    }

    private async Task<ToolOutcome> ExecuteBashCommand(string callId, string command)
    {
        if (_bashTool == null)
        {
            return ToolOutcome.Fail(callId, "Error: Bash tool not initialized");
        }

        try
        {
            var result = await _bashTool.ExecuteAsync(command);

            // Format result for the model
            var output = new System.Text.StringBuilder();

            if (!string.IsNullOrEmpty(result.Stdout))
            {
                output.AppendLine(result.Stdout);
            }

            if (!string.IsNullOrEmpty(result.Stderr))
            {
                output.AppendLine($"[stderr]\n{result.Stderr}");
            }

            output.AppendLine($"\n[exit code: {result.ExitCode}]");

            if (result.Truncated)
            {
                output.AppendLine("[output was truncated]");
            }

            if (result.TimedOut)
            {
                output.AppendLine("[command timed out]");
            }

            var outputStr = output.ToString().Trim();

            // Show brief status to user
            var statusColor = result.ExitCode == 0 ? UIColors.SpectreSuccess : UIColors.SpectreWarning;
            var statusIcon = result.ExitCode == 0 ? "✓" : "✗";
            AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon}[/] [{UIColors.SpectreMuted}]exit {result.ExitCode}[/]");

            // A non-zero exit is not flagged as a tool error — it's a normal shell
            // outcome the model should read, matching the prior "Error:"-prefix rule.
            return ToolOutcome.Ok(callId, outputStr);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error executing command: {ex.Message}");
        }
    }

    private Task<ToolOutcome> HandleWriteFileToolCall(string callId, string path, string content)
    {
        try
        {
            // Resolve path for display
            var fullPath = _writeFileTool?.ResolvePath(path) ?? path;

            // Guard: if file exists, it must have been read first
            if (File.Exists(fullPath))
            {
                if (!_fileReadTracker.HasBeenRead(fullPath))
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: You must read_file before overwriting an existing file."));

                if (_fileReadTracker.HasBeenModifiedSinceRead(fullPath))
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: File has been modified since you last read it. Read it again before writing."));
            }

            var lineCount = content.Split('\n').Length;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);

            // Auto-approve writes within cwd sandbox
            if (_bashTool != null)
            {
                var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, _bashTool.GetCwd());
                if (symlinkEscape)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
                    // Fall through to manual approval
                }
                else if (trusted)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• write {Markup.Escape(fullPath)} ({lineCount} lines)[/]");

                    if (_writeFileTool == null)
                        return Task.FromResult(ToolOutcome.Fail(callId, "Error: Write file tool not initialized"));

                    var writeResult = _writeFileTool.WriteFile(path, content);
                    if (writeResult.Success)
                    {
                        _fileReadTracker.RecordWrite(writeResult.Path);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]wrote {writeResult.BytesWritten} bytes[/]");
                        return Task.FromResult(ToolOutcome.Ok(callId, $"Successfully wrote {writeResult.BytesWritten} bytes to {writeResult.Path}"));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(writeResult.Error ?? "Unknown error")}[/]");
                        return Task.FromResult(ToolOutcome.Fail(callId, $"Error writing file: {writeResult.Error}"));
                    }
                }
            }

            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return Task.FromResult(DenyByPolicy(callId, "write_file (path outside working directory)"));
            if (NonInteractive)
                return Task.FromResult(DenyNonInteractive(callId, "write_file"));

            // Show approval prompt
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Write:[/] {Markup.Escape(fullPath)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  {lineCount} lines, {byteCount} bytes[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N/?]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    // Rejected (default is No for writes)
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Write rejected[/]");
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: User rejected file write. Permission denied."));
                }
                else if (key == '?')
                {
                    // Show content preview
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Content preview:[/]");
                    var preview = content.Length > 500 ? content[..500] + "\n... (truncated)" : content;
                    AnsiConsole.WriteLine(preview);
                    continue;
                }
                else if (key == 'y' || key == 'Y')
                {
                    // Approved - execute write
                    if (_writeFileTool == null)
                    {
                        return Task.FromResult(ToolOutcome.Fail(callId, "Error: Write file tool not initialized"));
                    }

                    var result = _writeFileTool.WriteFile(path, content);

                    if (result.Success)
                    {
                        _fileReadTracker.RecordWrite(result.Path);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]wrote {result.BytesWritten} bytes[/]");
                        return Task.FromResult(ToolOutcome.Ok(callId, $"Successfully wrote {result.BytesWritten} bytes to {result.Path}"));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                        return Task.FromResult(ToolOutcome.Fail(callId, $"Error writing file: {result.Error}"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Write error: {Markup.Escape(ex.Message)}[/]");
            return Task.FromResult(ToolOutcome.Fail(callId, $"Error during file write: {ex.Message}"));
        }
    }

    private async Task<ToolOutcome> HandleFetchUrlToolCall(string callId, string url)
    {
        try
        {
            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return DenyByPolicy(callId, "fetch_url");
            if (NonInteractive)
                return DenyNonInteractive(callId, "fetch_url");

            // Show approval prompt — network fetches always require explicit approval
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Fetch:[/] {Markup.Escape(url)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: outbound network request[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    var reason = AnsiConsole.Prompt(
                        new Spectre.Console.TextPrompt<string>($"[{UIColors.SpectreMuted}]Reason (optional):[/]")
                            .DefaultValue("User declined")
                            .AllowEmpty());
                    var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                        ? "Error: User rejected fetch_url. Permission denied."
                        : $"Error: User rejected fetch_url. Reason: {reason}";
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Fetch rejected[/]");
                    return ToolOutcome.Fail(callId, rejectionMessage);
                }
                else if (key == 'y' || key == 'Y')
                {
                    break;
                }
                // Any other key: loop and ask again
            }

            var result = await _fetchUrlTool!.FetchAsync(url);
            if (result.Success)
            {
                var chars = result.Content?.Length ?? 0;
                var truncNote = result.Truncated ? " (truncated)" : "";
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{chars:N0} chars{truncNote}[/]");
                return ToolOutcome.Ok(callId, result.Content ?? "");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                return ToolOutcome.Fail(callId, $"Error: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Fetch error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during fetch: {ex.Message}");
        }
    }

    private ToolOutcome HandleEditFileToolCall(string callId, string path, string oldString, string newString, bool replaceAll)
    {
        try
        {
            var fullPath = _editFileTool?.ResolvePath(path) ?? path;

            // Guard: file must have been read first
            if (!_fileReadTracker.HasBeenRead(fullPath))
                return ToolOutcome.Fail(callId, "Error: You must read_file before editing. Read the file first to see its current content.");

            if (_fileReadTracker.HasBeenModifiedSinceRead(fullPath))
                return ToolOutcome.Fail(callId, "Error: File has been modified since you last read it. Read it again before editing.");

            // Auto-approve edits within cwd sandbox
            if (_bashTool != null)
            {
                var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, _bashTool.GetCwd());
                if (symlinkEscape)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
                    // Fall through to manual approval
                }
                else if (trusted)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• edit {Markup.Escape(fullPath)}[/]");

                    if (_editFileTool == null)
                        return ToolOutcome.Fail(callId, "Error: Edit file tool not initialized");

                    var editResult = _editFileTool.EditFile(path, oldString, newString, replaceAll);
                    if (editResult.Success)
                    {
                        _fileReadTracker.RecordWrite(editResult.Path);
                        var msg = editResult.Replacements == 1
                            ? $"Successfully edited {editResult.Path} (1 replacement)"
                            : $"Successfully edited {editResult.Path} ({editResult.Replacements} replacements)";
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(msg)}[/]");
                        return ToolOutcome.Ok(callId, msg);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(editResult.Error ?? "Unknown error")}[/]");
                        return ToolOutcome.Fail(callId, $"Error: {editResult.Error}");
                    }
                }
            }

            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return DenyByPolicy(callId, "edit_file (path outside working directory)");
            if (NonInteractive)
                return DenyNonInteractive(callId, "edit_file");

            // Show approval prompt with diff preview
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Edit:[/] {Markup.Escape(fullPath)}");

            var oldPreview = oldString.Length > 200 ? oldString[..200] + "..." : oldString;
            var newPreview = newString.Length > 200 ? newString[..200] + "..." : newString;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  - {Markup.Escape(oldPreview)}[/]");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]  + {Markup.Escape(newPreview)}[/]");
            if (replaceAll)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  (replace all occurrences)[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Edit rejected[/]");
                    return ToolOutcome.Fail(callId, "Error: User rejected file edit. Permission denied.");
                }
                else if (key == 'y' || key == 'Y')
                {
                    if (_editFileTool == null)
                        return ToolOutcome.Fail(callId, "Error: Edit file tool not initialized");

                    var result = _editFileTool.EditFile(path, oldString, newString, replaceAll);

                    if (result.Success)
                    {
                        _fileReadTracker.RecordWrite(result.Path);
                        var msg = result.Replacements == 1
                            ? $"Successfully edited {result.Path} (1 replacement)"
                            : $"Successfully edited {result.Path} ({result.Replacements} replacements)";
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(msg)}[/]");
                        return ToolOutcome.Ok(callId, msg);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                        return ToolOutcome.Fail(callId, $"Error: {result.Error}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Edit error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during file edit: {ex.Message}");
        }
    }

    private ToolOutcome HandleApplyPatchToolCall(string callId, string input)
    {
        if (_applyPatchTool == null)
            return ToolOutcome.Fail(callId, "Error: apply_patch tool not initialized");

        List<FileOp> ops;
        try
        {
            ops = PatchParser.Parse(input);
        }
        catch (PatchParseException ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch parse error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error parsing patch: {ex.Message}");
        }

        var cwd = _applyPatchTool.GetCwd();

        PatchPreview preview;
        try
        {
            preview = PatchApplier.BuildPreview(ops, cwd, _fileReadTracker);
        }
        catch (PatchApplyException ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error: {ex.Message}");
        }

        // Check sandbox for every affected absolute path — both source (for updates/deletes) and destination (for moves/adds).
        var allTrusted = true;
        var anyEscape = false;
        for (int i = 0; i < ops.Count; i++)
        {
            var touched = new List<string> { preview.Files[i].FinalPath };
            if (ops[i] is UpdateFile upd && upd.MoveTo != null)
            {
                var sourceFull = Path.IsPathRooted(ops[i].Path)
                    ? Path.GetFullPath(ops[i].Path)
                    : Path.GetFullPath(Path.Combine(cwd, ops[i].Path));
                touched.Add(sourceFull);
            }
            foreach (var p in touched)
            {
                var (trusted, escape) = TrustSandbox.CheckPath(p, cwd);
                if (escape) anyEscape = true;
                if (!trusted) allTrusted = false;
            }
        }

        RenderPatchSummary(preview);

        if (anyEscape)
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ One or more paths resolve outside working directory via symlink — manual approval required[/]");

        if (allTrusted && !anyEscape)
        {
            try
            {
                PatchApplier.Apply(preview, ops, cwd, _fileReadTracker);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch failed: {Markup.Escape(ex.Message)}[/]");
                return ToolOutcome.Fail(callId, $"Error applying patch: {ex.Message}");
            }
            var summary = BuildApplySummary(preview);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(summary)}[/]");
            return ToolOutcome.Ok(callId, summary);
        }

        if (_approvalPolicy.Default == ApprovalDefault.Deny)
            return DenyByPolicy(callId, "apply_patch (path outside working directory)");
        if (NonInteractive)
            return DenyNonInteractive(callId, "apply_patch (path outside working directory)");

        // Flush any pending input
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);

        while (true)
        {
            AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();

            if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Patch rejected[/]");
                return ToolOutcome.Fail(callId, "Error: User rejected apply_patch. Permission denied.");
            }
            else if (key == 'y' || key == 'Y')
            {
                try
                {
                    PatchApplier.Apply(preview, ops, cwd, _fileReadTracker);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch failed: {Markup.Escape(ex.Message)}[/]");
                    return ToolOutcome.Fail(callId, $"Error applying patch: {ex.Message}");
                }
                var summary = BuildApplySummary(preview);
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(summary)}[/]");
                return ToolOutcome.Ok(callId, summary);
            }
        }
    }

    private static void RenderPatchSummary(PatchPreview preview)
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Patch:[/] {preview.Files.Count} file(s)");
        foreach (var fp in preview.Files)
        {
            var (glyph, label) = fp.Kind switch
            {
                FileOpKind.Add => ("+", "add"),
                FileOpKind.Delete => ("-", "delete"),
                FileOpKind.Update => ("~", "update"),
                FileOpKind.UpdateAndMove => ("↺", "rename+update"),
                _ => ("?", "?"),
            };
            var stats = fp.Kind switch
            {
                FileOpKind.Add => $"{fp.NewLineCount} lines",
                FileOpKind.Delete => $"{fp.OldLineCount} lines",
                _ => $"-{fp.OldLineCount}/+{fp.NewLineCount}",
            };
            var path = fp.Kind == FileOpKind.UpdateAndMove
                ? $"{fp.OriginalPath} → {fp.FinalPath}"
                : fp.FinalPath;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  {glyph} {label,-13} {Markup.Escape(path)} ({stats})[/]");
        }
    }

    private static string BuildApplySummary(PatchPreview preview)
    {
        var added = preview.Files.Count(f => f.Kind == FileOpKind.Add);
        var updated = preview.Files.Count(f => f.Kind == FileOpKind.Update || f.Kind == FileOpKind.UpdateAndMove);
        var deleted = preview.Files.Count(f => f.Kind == FileOpKind.Delete);
        return $"Applied patch: {added} added, {updated} updated, {deleted} deleted";
    }

    private static readonly JsonSerializerOptions _verboseJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void LogToolCall(string toolName, IDictionary<string, object?>? arguments, string result)
    {
        if (!_verbose) return;

        var argsJson = arguments != null
            ? JsonSerializer.Serialize(arguments, _verboseJsonOptions)
            : "{}";

        // Unescape Unicode sequences for readability (e.g., \u0022 -> ")
        var displayResult = System.Text.RegularExpressions.Regex.Unescape(result);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]┌─ Tool: {Markup.Escape(toolName)}[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]│ Input: {Markup.Escape(argsJson)}[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]└ Output: {Markup.Escape(displayResult)}[/]");
    }

    /// <summary>
    /// Checks if a path is within the trust sandbox (cwd/temp). If outside, prompts the user.
    /// Returns true if the access is approved, false if rejected.
    /// </summary>
    private bool ApproveReadPath(string toolLabel, string fullPath, string cwd)
    {
        var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, cwd);
        var decision = _approvalPolicy.DecidePath(trusted && !symlinkEscape);
        if (decision == ApprovalDecision.Allow) return true;

        if (symlinkEscape)
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]{toolLabel}:[/] {Markup.Escape(fullPath)}");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: path is outside working directory[/]");

        // No terminal to prompt at, or the policy default is deny: refuse the
        // out-of-sandbox access deterministically.
        if (decision == ApprovalDecision.Deny || NonInteractive)
        {
            Console.Error.WriteLine($"[nb] denied: {toolLabel} on a path outside the working directory ({(NonInteractive ? "non-interactive session" : "approval policy default is deny")}).");
            return false;
        }

        while (Console.KeyAvailable) Console.ReadKey(intercept: true);

        while (true)
        {
            AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();
            if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{toolLabel} rejected[/]");
                return false;
            }
            if (key == 'y' || key == 'Y') return true;
        }
    }

    private int EstimateTokenCount()
    {
        long totalChars = 0;
        foreach (var message in _conversationHistory)
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
        _todoManager.Reset();
        _lastRemindedTodos = null;

        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Conversation history cleared[/]");
    }

    // Enumerates the tools currently exposed to the model, grouped by source with
    // each tool's effective approval status. Mirrors the assembly in
    // SendMessageInternalAsync — keep the two in sync when adding/removing tools.
    public IReadOnlyList<ToolDescriptor> GetAvailableTools()
    {
        var tools = new List<ToolDescriptor>();

        // MCP tools from all connected servers.
        var mcpTools = _mcpManager.GetTools();
        foreach (var tool in mcpTools)
        {
            var approval = _mcpManager.IsAlwaysAllowed(tool.Name) ? "auto (always-allow)" : "prompt";
            if (_fakeToolManager.GetFakeTool(tool.Name) != null) approval += " · faked";
            tools.Add(new ToolDescriptor("MCP", tool.Name, approval));
        }
        if (mcpTools.Count > 0)
        {
            tools.Add(new ToolDescriptor("Resources", ResourceTools.CreateListResourcesTool(_mcpManager).Name, "auto"));
            tools.Add(new ToolDescriptor("Resources", ResourceTools.CreateReadResourceTool(_mcpManager).Name, "auto"));
        }

        // Native tools (all null under --nobash). Read-only tools auto-approve within
        // the cwd sandbox; writes and bash auto-approve only with trust mode.
        var write = _trustMode ? "auto (trust)" : "prompt";
        if (_readFileTool != null) tools.Add(new ToolDescriptor("Native", _readFileTool.CreateTool().Name, "auto (cwd)"));
        if (_listDirTool != null) tools.Add(new ToolDescriptor("Native", _listDirTool.CreateTool().Name, "auto (cwd)"));
        if (_findFilesTool != null) tools.Add(new ToolDescriptor("Native", _findFilesTool.CreateTool().Name, "auto (cwd)"));
        if (_grepTool != null) tools.Add(new ToolDescriptor("Native", _grepTool.CreateTool().Name, "auto (cwd)"));
        if (_bashTool != null) tools.Add(new ToolDescriptor("Native", _bashTool.CreateTool().Name, write));
        if (_writeFileTool != null) tools.Add(new ToolDescriptor("Native", _writeFileTool.CreateTool().Name, write));
        if (_editFileTool != null) tools.Add(new ToolDescriptor("Native", _editFileTool.CreateTool().Name, write));
        if (_applyPatchTool != null) tools.Add(new ToolDescriptor("Native", _applyPatchTool.CreateTool().Name, write));
        if (_fetchUrlTool != null) tools.Add(new ToolDescriptor("Native", _fetchUrlTool.CreateTool().Name, "prompt"));

        // Todo tools ride the native surface (dropped by `tools -todo` / `tools none`).
        if (_toolSurface.AllowsNative("todo"))
        {
            tools.Add(new ToolDescriptor("Todo", _todoTool.CreateWriteTool().Name, "auto"));
            tools.Add(new ToolDescriptor("Todo", _todoTool.CreateReadTool().Name, "auto"));
        }

        // Fake tools that stand alone (overrides already show under their MCP server)
        var seen = tools.Select(t => t.Name).ToHashSet();
        foreach (var name in _fakeToolManager.GetFakeToolNames())
            if (seen.Add(name))
                tools.Add(new ToolDescriptor("Fake", name, "auto (faked)"));

        return tools;
    }
}

public record ToolDescriptor(string Group, string Name, string Approval);