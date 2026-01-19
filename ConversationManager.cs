using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.AI;
using nb.MCP;
using nb.Shell;
using nb.Utilities;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace nb;

public class ConversationManager
{
    private const int MAX_TOOL_CALLS_PER_MESSAGE = 10;
    private static readonly TimeSpan McpToolTimeout = TimeSpan.FromSeconds(60);

    private IChatClient _client;
    private readonly McpManager _mcpManager;
    private readonly FakeToolManager _fakeToolManager;
    private readonly BashTool? _bashTool;
    private readonly WriteFileTool? _writeFileTool;
    private readonly ApprovalPatterns _approvalPatterns;
    private readonly bool _verbose;
    private readonly List<AIChatMessage> _conversationHistory = new();
    private bool _stopSpinner = false;
    private int _toolCallCount = 0;
    private string _currentProviderName = "";

    public ConversationManager(
        IChatClient client,
        McpManager mcpManager,
        FakeToolManager fakeToolManager,
        BashTool? bashTool,
        WriteFileTool? writeFileTool,
        ApprovalPatterns approvalPatterns,
        string providerName = "",
        bool verbose = false)
    {
        _client = client;
        _mcpManager = mcpManager;
        _fakeToolManager = fakeToolManager;
        _bashTool = bashTool;
        _writeFileTool = writeFileTool;
        _approvalPatterns = approvalPatterns;
        _currentProviderName = providerName;
        _verbose = verbose;
    }

    public void SwitchProvider(IChatClient newClient, string providerName)
    {
        _client = newClient;
        _currentProviderName = providerName;
        Console.WriteLine($"Switched to provider: {providerName}");
    }

    public string GetCurrentProvider() => _currentProviderName;

    public void InitializeWithSystemPrompt(string systemPrompt)
    {
        _conversationHistory.Add(new AIChatMessage(ChatRole.System, systemPrompt));
    }


    public void AddToConversationHistory(string userMessage)
    {
        _conversationHistory.Add(new AIChatMessage(ChatRole.User, userMessage));
        _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, "I've received the document content and it's now available in our conversation context."));
    }
    
    public void AddImageToConversationHistory(string description, byte[] imageData, string mimeType)
    {
        var textContent = new TextContent(description);
        var imageContent = new DataContent(imageData, mimeType);
        var contentList = new List<AIContent> { textContent, imageContent };
        
        _conversationHistory.Add(new AIChatMessage(ChatRole.User, contentList));
        _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, "I've received the image and it's now available in our conversation context. I can analyze and discuss its contents."));
    }

    public async Task SendMessageAsync(string userMessage)
    {
        if (_client == null) return;

        // Reset tool call counter for new message
        _toolCallCount = 0;

        // Add user message to conversation history (no automatic RAG injection)
        _conversationHistory.Add(new AIChatMessage(ChatRole.User, userMessage));
        
        await SendMessageInternalAsync();
    }

    private async Task SendMessageInternalAsync()
    {
        if (_client == null) return;

        try
        {
            var requestOptions = new ChatOptions()
            {
                MaxOutputTokens = 10000,
            };

            // Add MCP tools, native resource tools, bash tools, and fake tools
            var mcpTools = _mcpManager.GetTools().ToList();

            // Add native resource tools
            mcpTools.Add(ResourceTools.CreateListResourcesTool(_mcpManager));
            mcpTools.Add(ResourceTools.CreateReadResourceTool(_mcpManager));

            // Add bash tools if enabled
            if (_bashTool != null)
            {
                mcpTools.Add(_bashTool.CreateTool());
                mcpTools.Add(_bashTool.CreateSetCwdTool());
            }

            if (_writeFileTool != null)
            {
                mcpTools.Add(_writeFileTool.CreateTool());
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

            // Start spinner and make async API call
            var spinnerTask = Task.Run(() => ShowSpinner());
            var apiTask = _client.GetResponseAsync(_conversationHistory, requestOptions);

            var response = await apiTask;

            // Stop spinner
            _stopSpinner = true;
            await spinnerTask;

            // Handle tool calls if present - check if any message has tool calls
            var hasToolCalls = response.Messages.Any(m => m.Contents.Any(c => c is FunctionCallContent));
            if (hasToolCalls)
            {
                // Check if we've exceeded max tool calls
                if (_toolCallCount >= MAX_TOOL_CALLS_PER_MESSAGE)
                {
                    var limitMessage = "I've reached the maximum number of tool calls for this message. Let me provide a response with the information I have.";
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, limitMessage));
                    RenderMarkdown(limitMessage);
                    return;
                }

                // Add assistant message with tool calls to history
                _conversationHistory.AddRange(response.Messages);

                // Collect all tool results in a single list
                var allToolResults = new List<AIContent>();

                // Execute tool calls
                foreach (var message in response.Messages)
                {
                    foreach (var functionCall in message.Contents.OfType<FunctionCallContent>())
                    {
                        try
                        {
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

                                    Console.WriteLine($"calling {functionCall.Name}");
                                    try
                                    {
                                        var result = await resourceTool.InvokeAsync(arguments).AsTask().WaitAsync(McpToolTimeout);
                                        var resultString = result?.ToString() ?? string.Empty;
                                        allToolResults.Add(new FunctionResultContent(functionCall.CallId, resultString));
                                        LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                                    }
                                    catch (TimeoutException)
                                    {
                                        var errorMsg = $"Error: Tool '{functionCall.Name}' timed out after {McpToolTimeout.TotalSeconds}s";
                                        allToolResults.Add(new FunctionResultContent(functionCall.CallId, errorMsg));
                                        Console.WriteLine(errorMsg);
                                    }
                                }
                                else
                                {
                                    var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                    allToolResults.Add(new FunctionResultContent(functionCall.CallId, errorMsg));
                                    Console.WriteLine(errorMsg);
                                }
                            }
                            // Check if this is a bash tool (custom approval UX)
                            else if (functionCall.Name == "bash" && _bashTool != null)
                            {
                                var description = functionCall.Arguments?["description"]?.ToString() ?? "";
                                var command = functionCall.Arguments?["command"]?.ToString() ?? "";
                                var result = await HandleBashToolCall(functionCall.CallId, command, description);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Result?.ToString() ?? "");
                            }
                            // Check if this is write_file (custom approval UX)
                            else if (functionCall.Name == "write_file" && _writeFileTool != null)
                            {
                                var path = functionCall.Arguments?["path"]?.ToString() ?? "";
                                var content = functionCall.Arguments?["content"]?.ToString() ?? "";
                                var result = await HandleWriteFileToolCall(functionCall.CallId, path, content);
                                allToolResults.Add(result);
                                LogToolCall(functionCall.Name, functionCall.Arguments, result.Result?.ToString() ?? "");
                            }
                            // Check if this is set_cwd (no approval needed)
                            else if (functionCall.Name == "set_cwd" && _bashTool != null)
                            {
                                var bashSetCwdTool = allTools.FirstOrDefault(t => t.Name == "set_cwd");
                                if (bashSetCwdTool != null)
                                {
                                    var arguments = new AIFunctionArguments();
                                    if (functionCall.Arguments != null)
                                    {
                                        foreach (var kvp in functionCall.Arguments)
                                        {
                                            arguments[kvp.Key] = kvp.Value?.ToString();
                                        }
                                    }

                                    Console.WriteLine($"calling {functionCall.Name}");
                                    var result = await bashSetCwdTool.InvokeAsync(arguments);
                                    var resultString = result?.ToString() ?? string.Empty;
                                    allToolResults.Add(new FunctionResultContent(functionCall.CallId, resultString));
                                    Console.WriteLine($"  -> {resultString}");
                                    LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                                }
                                else
                                {
                                    var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                    allToolResults.Add(new FunctionResultContent(functionCall.CallId, errorMsg));
                                    Console.WriteLine(errorMsg);
                                }
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

                                Console.WriteLine($"Fake tool invoked: {functionCall.Name}");
                                Console.WriteLine($"   Parameters: {argumentsJson}");
                                Console.WriteLine($"   -> {fakeTool.Response}");

                                allToolResults.Add(new FunctionResultContent(functionCall.CallId, fakeTool.Response));
                                LogToolCall(functionCall.Name, functionCall.Arguments, fakeTool.Response);
                            }
                            else
                            {
                                // Handle MCP tools - check approval
                                var mcpTool = mcpTools.FirstOrDefault(t => t.Name == functionCall.Name);
                                if (mcpTool != null)
                                {
                                    // Check if tool is in always-allow list
                                    bool approved = _mcpManager.IsAlwaysAllowed(functionCall.Name);

                                    if (!approved)
                                    {
                                        // Show tool call details and request approval
                                        var argumentsJson = JsonSerializer.Serialize(functionCall.Arguments, new JsonSerializerOptions { WriteIndented = true });

                                        while (true)
                                        {
                                            Console.WriteLine($"Allow tool call: {functionCall.Name}? (Y/n/?)");
                                            var key = Console.ReadKey().KeyChar;

                                            if (key == 'n')
                                            {
                                                approved = false;
                                                break;
                                            }
                                            else if (key == '?' )
                                            {
                                                Console.WriteLine("Arguments:");
                                                Console.WriteLine(argumentsJson);
                                                Console.Write("Allow this call? [Y/n] ");
                                                var confirm = Console.ReadLine()?.Trim().ToLowerInvariant();
                                                approved = string.IsNullOrEmpty(confirm) || confirm.StartsWith('y');
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
                                        Console.Write("Reason for rejection (optional): ");
                                        var reason = Console.ReadLine() ?? "";
                                        if (string.IsNullOrWhiteSpace(reason)) reason = "User declined";

                                        var rejectionMessage = reason == "User declined"
                                            ? "Error: User rejected this tool call. Permission denied. Do not retry this action."
                                            : $"Error: User rejected this tool call. Reason: {reason}. Please consider an alternative approach based on the user's feedback.";

                                        allToolResults.Add(new FunctionResultContent(functionCall.CallId, rejectionMessage));

                                        Console.WriteLine("Tool call rejected, notifying model");
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

                                    Console.WriteLine($"calling {functionCall.Name}");
                                    try
                                    {
                                        var result = await mcpTool.InvokeAsync(arguments).AsTask().WaitAsync(McpToolTimeout);
                                        var resultString = result?.ToString() ?? string.Empty;
                                        allToolResults.Add(new FunctionResultContent(functionCall.CallId, resultString));
                                        LogToolCall(functionCall.Name, functionCall.Arguments, resultString);
                                    }
                                    catch (TimeoutException)
                                    {
                                        var errorMsg = $"Error: Tool '{functionCall.Name}' timed out after {McpToolTimeout.TotalSeconds}s";
                                        allToolResults.Add(new FunctionResultContent(functionCall.CallId, errorMsg));
                                        Console.WriteLine(errorMsg);
                                    }
                                }
                                else
                                {
                                    var errorMsg = $"Error: Tool '{functionCall.Name}' not found";
                                    allToolResults.Add(new FunctionResultContent(functionCall.CallId, errorMsg));
                                    Console.WriteLine(errorMsg);
                                }
                            }

                            // Increment tool call counter
                            _toolCallCount++;
                        }
                        catch (Exception ex)
                        {
                            allToolResults.Add(new FunctionResultContent(functionCall.CallId, $"Error: {ex.Message}"));
                        }
                    }
                }

                // Add all tool results as a single message
                if (allToolResults.Count > 0)
                {
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Tool, allToolResults));
                }

                // Get another response after tool execution
                await SendMessageInternalAsync();
            }
            else
            {
                var assistantMessage = response.Text ?? string.Empty;

                if (!string.IsNullOrEmpty(assistantMessage))
                {
                    // Add assistant response to conversation history
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, assistantMessage));
                    RenderMarkdown(assistantMessage);
                }
            }
        }
        catch (Exception ex)
        {
            // Stop spinner on error
            _stopSpinner = true;
            Console.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            // Reset spinner flag for next call
            _stopSpinner = false;
        }
    }


    private void ShowSpinner()
    {
        // Skip if stdout is redirected (e.g., piped to file)
        if (Console.IsOutputRedirected)
            return;

        try
        {
            Console.Write("Thinking...");

            while (!_stopSpinner)
            {
                Thread.Sleep(100);
            }

            // Clear the line
            Console.Write("\r            \r");
        }
        catch
        {
            // Ignore console errors when output is redirected
        }
    }

    private static void RenderMarkdown(string markdown)
    {
        Console.WriteLine(markdown);
    }

    private async Task<FunctionResultContent> HandleBashToolCall(string callId, string command, string description)
    {
        try
        {
            // Classify the command for display
            var classified = CommandClassifier.Classify(command);

            // Check if pre-approved via --approve flag
            if (_approvalPatterns.IsApproved(command))
            {
                Console.WriteLine($"bash (pre-approved): {classified.DisplayText}");
                return await ExecuteBashCommand(callId, command);
            }

            // Show model's description of intent (if provided)
            if (!string.IsNullOrWhiteSpace(description))
            {
                Console.WriteLine(description);
            }

            // Show approval prompt with command classification
            Console.WriteLine($"{classified.Category}: {classified.DisplayText}");

            if (classified.IsDangerous && classified.DangerReason != null)
            {
                Console.WriteLine($"  Warning: {classified.DangerReason}");
            }

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
                Console.Write($"Execute? {options} ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || (!defaultYes && (key == '\r' || key == '\n')))
                {
                    // Rejected
                    Console.Write("Reason (optional): ");
                    var reason = Console.ReadLine() ?? "";
                    if (string.IsNullOrWhiteSpace(reason)) reason = "User declined";

                    var rejectionMessage = reason == "User declined"
                        ? "Error: User rejected this command. Permission denied."
                        : $"Error: User rejected this command. Reason: {reason}";

                    Console.WriteLine("Command rejected");
                    return new FunctionResultContent(callId, rejectionMessage);
                }
                else if (key == '?')
                {
                    // Show full command
                    Console.WriteLine("Full command:");
                    Console.WriteLine(command);
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
            Console.WriteLine($"Approval error: {ex.Message}");
            return new FunctionResultContent(callId, $"Error during command approval: {ex.Message}");
        }
    }

    private async Task<FunctionResultContent> ExecuteBashCommand(string callId, string command)
    {
        if (_bashTool == null)
        {
            return new FunctionResultContent(callId, "Error: Bash tool not initialized");
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
            var statusIcon = result.ExitCode == 0 ? "ok" : "fail";
            Console.WriteLine($"[{statusIcon}] exit {result.ExitCode}");

            return new FunctionResultContent(callId, outputStr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return new FunctionResultContent(callId, $"Error executing command: {ex.Message}");
        }
    }

    private Task<FunctionResultContent> HandleWriteFileToolCall(string callId, string path, string content)
    {
        try
        {
            // Resolve path for display
            var fullPath = _writeFileTool?.ResolvePath(path) ?? path;

            var lineCount = content.Split('\n').Length;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);

            // Show approval prompt
            Console.WriteLine($"Write: {fullPath}");
            Console.WriteLine($"  {lineCount} lines, {byteCount} bytes");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                Console.Write("Execute? [y/N/?] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    // Rejected (default is No for writes)
                    Console.WriteLine("Write rejected");
                    return Task.FromResult(new FunctionResultContent(callId, "Error: User rejected file write. Permission denied."));
                }
                else if (key == '?')
                {
                    // Show content preview
                    Console.WriteLine("Content preview:");
                    var preview = content.Length > 500 ? content[..500] + "\n... (truncated)" : content;
                    Console.WriteLine(preview);
                    continue;
                }
                else if (key == 'y' || key == 'Y')
                {
                    // Approved - execute write
                    if (_writeFileTool == null)
                    {
                        return Task.FromResult(new FunctionResultContent(callId, "Error: Write file tool not initialized"));
                    }

                    var result = _writeFileTool.WriteFile(path, content);

                    if (result.Success)
                    {
                        Console.WriteLine($"[ok] wrote {result.BytesWritten} bytes");
                        return Task.FromResult(new FunctionResultContent(callId, $"Successfully wrote {result.BytesWritten} bytes to {result.Path}"));
                    }
                    else
                    {
                        Console.WriteLine($"[fail] {result.Error ?? "Unknown error"}");
                        return Task.FromResult(new FunctionResultContent(callId, $"Error writing file: {result.Error}"));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Write error: {ex.Message}");
            return Task.FromResult(new FunctionResultContent(callId, $"Error during file write: {ex.Message}"));
        }
    }

    public async Task SaveConversationHistoryAsync(string filePath = ".nb_conversation_history.json")
    {
        try
        {
            // Convert conversation history to a serializable format
            var serializableHistory = _conversationHistory.Select(msg => new
            {
                Type = msg.Role.Value switch
                {
                    "user" => "UserChatMessage",
                    "assistant" => "AssistantChatMessage",
                    "system" => "SystemChatMessage",
                    _ => msg.Role.Value
                },
                Content = ExtractMessageContent(msg)
            }).ToArray();

            var json = JsonSerializer.Serialize(serializableHistory, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not save conversation history: {ex.Message}");
        }
    }

    public async Task LoadConversationHistoryAsync(string filePath = ".nb_conversation_history.json")
    {
        try
        {
            if (!File.Exists(filePath))
                return; // No history to load

            var json = await File.ReadAllTextAsync(filePath);
            var historyData = JsonSerializer.Deserialize<JsonElement[]>(json);

            // Clear existing history except system message (keep the first message if it's System role)
            var systemMessage = _conversationHistory.FirstOrDefault(msg => msg.Role == ChatRole.System);
            _conversationHistory.Clear();
            
            if (systemMessage != null)
            {
                _conversationHistory.Add(systemMessage);
            }

            // Reconstruct conversation history
            if (historyData != null)
            {
                foreach (var item in historyData)
                {
                    var type = item.GetProperty("Type").GetString();
                    var content = item.GetProperty("Content").GetString();

                if (string.IsNullOrEmpty(content) || type == "SystemChatMessage")
                    continue; // Skip empty content or system messages (already handled)

                switch (type)
                {
                    case "UserChatMessage":
                        _conversationHistory.Add(new AIChatMessage(ChatRole.User, content));
                        break;
                    case "AssistantChatMessage":
                        _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, content));
                        break;
                    // Note: We skip ToolChatMessage as they're complex and transient
                }
            }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load conversation history: {ex.Message}");
        }
    }

    private static string ExtractMessageContent(AIChatMessage message)
    {
        // Extract text content from Microsoft.Extensions.AI ChatMessage
        var textContent = message.Contents?.OfType<TextContent>().FirstOrDefault();
        return textContent?.Text ?? "";
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

        Console.WriteLine($"--- Tool: {toolName}");
        Console.WriteLine($"  Input: {argsJson}");
        Console.WriteLine($"  Output: {displayResult}");
    }

    public void ClearConversationHistory()
    {
        // Keep only the system message (first message if it's System role)
        var systemMessage = _conversationHistory.FirstOrDefault(msg => msg.Role == ChatRole.System);
        _conversationHistory.Clear();
        
        if (systemMessage != null)
        {
            _conversationHistory.Add(systemMessage);
        }
        
        Console.WriteLine("Conversation history cleared");
    }
}