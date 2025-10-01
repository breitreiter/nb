using System.Text.Json;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Spectre.Console;
using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace nb;

public class ConversationManager
{
    private const int MAX_TOOL_CALLS_PER_MESSAGE = 3;
    
    private readonly IChatClient _client;
    private readonly McpManager _mcpManager;
    private readonly FakeToolManager _fakeToolManager;
    private readonly List<AIChatMessage> _conversationHistory = new();
    private bool _stopSpinner = false;
    private int _toolCallCount = 0;

    public ConversationManager(IChatClient client, McpManager mcpManager, FakeToolManager fakeToolManager)
    {
        _client = client;
        _mcpManager = mcpManager;
        _fakeToolManager = fakeToolManager;
    }

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
            // Support for this recently-launched model with MaxOutputTokenCount parameter requires
            // Azure.AI.OpenAI 2.2.0-beta.4 and SetNewMaxCompletionTokensPropertyEnabled
            var requestOptions = new ChatOptions()
            {
                MaxOutputTokens = 10000,
            };

            // Add MCP tools and fake tools (fake tools override MCP tools with same name)
            var mcpTools = _mcpManager.GetTools();
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
                    _conversationHistory.Add(new AIChatMessage(ChatRole.Assistant, "I've reached the maximum number of tool calls for this message. Let me provide a response with the information I have."));
                    return;
                }

                // Add assistant message with tool calls to history
                _conversationHistory.AddRange(response.Messages);

                // Execute tool calls
                foreach (var message in response.Messages)
                {
                    foreach (var functionCall in message.Contents.OfType<FunctionCallContent>())
                    {
                        try
                        {
                            // Check if this is a fake tool first
                            var fakeTool = _fakeToolManager.GetFakeTool(functionCall.Name);
                            if (fakeTool != null)
                            {
                                // Handle fake tool
                                var argumentsJson = JsonSerializer.Serialize(functionCall.Arguments);
                                
                                // Show fake tool invocation notification
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Fake tool invoked: {functionCall.Name}[/]");
                                AnsiConsole.MarkupLine($"[dim grey]   Parameters: {argumentsJson}[/]");
                                AnsiConsole.MarkupLine($"[dim grey]   → {fakeTool.Response}[/]");
                                
                                var fakeToolContent = new List<AIContent> { new FunctionResultContent(functionCall.CallId, fakeTool.Response) };
                                _conversationHistory.Add(new AIChatMessage(ChatRole.Tool, fakeToolContent));
                            }
                            else
                            {
                                // Handle MCP tools
                                var mcpTool = mcpTools.FirstOrDefault(t => t.Name == functionCall.Name);
                                if (mcpTool != null)
                                {
                                    var arguments = new AIFunctionArguments();
                                    foreach (var kvp in functionCall.Arguments)
                                    {
                                        arguments[kvp.Key] = kvp.Value?.ToString();
                                    }
                                    
                                    var result = await mcpTool.InvokeAsync(arguments);
                                    var mcpToolContent = new List<AIContent> { new FunctionResultContent(functionCall.CallId, result.ToString()) };
                                    _conversationHistory.Add(new AIChatMessage(ChatRole.Tool, mcpToolContent));
                                    
                                    AnsiConsole.MarkupLine($"[dim grey]• calling {functionCall.Name}[/]");
                                }
                            }
                            
                            // Increment tool call counter
                            _toolCallCount++;
                        }
                        catch (Exception ex)
                        {
                            var errorContent = new List<AIContent> { new FunctionResultContent(functionCall.CallId, $"Error: {ex.Message}") };
                            _conversationHistory.Add(new AIChatMessage(ChatRole.Tool, errorContent));
                        }
                    }
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
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: {ex.Message}[/]");
        }
        finally
        {
            // Reset spinner flag for next call
            _stopSpinner = false;
        }
    }


    private void ShowSpinner()
    {
        // Skip spinner if stdout is redirected (e.g., piped to file)
        if (Console.IsOutputRedirected)
            return;
            
        var spinnerChars = new char[] { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };
        int index = 0;
        
        try
        {
            Console.CursorVisible = false;
            
            while (!_stopSpinner)
            {
                Console.Write($"\r{spinnerChars[index]} Thinking...");
                index = (index + 1) % spinnerChars.Length;
                Thread.Sleep(100);
            }
            
            Console.Write("\r                    \r"); // Clear the spinner line
            Console.CursorVisible = true;
        }
        catch
        {
            // Ignore console errors when output is redirected
        }
    }

    private static void RenderMarkdown(string markdown)
    {
        AnsiConsole.WriteLine(markdown);
    }

    public async Task SaveConversationHistoryAsync(string filePath = ".nb_conversation_history.json")
    {
        try
        {
            // Convert conversation history to a serializable format
            var serializableHistory = _conversationHistory.Select(msg => new
            {
                Type = msg.GetType().Name,
                Content = ExtractMessageContent(msg)
            }).ToArray();

            var json = JsonSerializer.Serialize(serializableHistory, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: Could not save conversation history: {ex.Message}[/]");
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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: Could not load conversation history: {ex.Message}[/]");
        }
    }

    private static string ExtractMessageContent(AIChatMessage message)
    {
        // Extract text content from Microsoft.Extensions.AI ChatMessage
        var textContent = message.Contents?.OfType<TextContent>().FirstOrDefault();
        return textContent?.Text ?? "";
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
        
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Conversation history cleared[/]");
    }
}