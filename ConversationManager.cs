using System.Text.Json;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Spectre.Console;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace nb;

public class ConversationManager
{
    private const int MAX_TOOL_CALLS_PER_MESSAGE = 3;
    
    private readonly ChatClient _client;
    private readonly McpManager _mcpManager;
    private readonly FakeToolManager _fakeToolManager;
    private readonly List<OpenAIChatMessage> _conversationHistory = new();
    private bool _stopSpinner = false;
    private int _toolCallCount = 0;

    public ConversationManager(ChatClient client, McpManager mcpManager, FakeToolManager fakeToolManager)
    {
        _client = client;
        _mcpManager = mcpManager;
        _fakeToolManager = fakeToolManager;
    }

    public void InitializeWithSystemPrompt(string systemPrompt)
    {
        _conversationHistory.Add(new SystemChatMessage(systemPrompt));
    }


    public void AddToConversationHistory(string userMessage)
    {
        _conversationHistory.Add(new UserChatMessage(userMessage));
        _conversationHistory.Add(new AssistantChatMessage("I've received the document content and it's now available in our conversation context."));
    }
    
    public void AddImageToConversationHistory(string description, byte[] imageData, string mimeType)
    {
        var textPart = ChatMessageContentPart.CreateTextPart(description);
        var imagePart = ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageData), mimeType);
        
        _conversationHistory.Add(new UserChatMessage(textPart, imagePart));
        _conversationHistory.Add(new AssistantChatMessage("I've received the image and it's now available in our conversation context. I can analyze and discuss its contents."));
    }

    public async Task SendMessageAsync(string userMessage)
    {
        if (_client == null) return;

        // Reset tool call counter for new message
        _toolCallCount = 0;

        // Add user message to conversation history (no automatic RAG injection)
        _conversationHistory.Add(new UserChatMessage(userMessage));
        
        await SendMessageInternalAsync();
    }

    private async Task SendMessageInternalAsync()
    {
        if (_client == null) return;

        try
        {
            // Support for this recently-launched model with MaxOutputTokenCount parameter requires
            // Azure.AI.OpenAI 2.2.0-beta.4 and SetNewMaxCompletionTokensPropertyEnabled
            var requestOptions = new ChatCompletionOptions()
            {
                MaxOutputTokenCount = 10000,
            };

            // Add MCP tools and fake tools (fake tools override MCP tools with same name)
            var mcpTools = _mcpManager.GetTools();
            var allTools = _fakeToolManager.IntegrateWithMcpTools(mcpTools);
            if (allTools.Count > 0)
            {
                foreach (var tool in allTools)
                {
                    var chatTool = ConvertAIFunctionToChatTool(tool);
                    requestOptions.Tools.Add(chatTool);
                }
            }


            // The SetNewMaxCompletionTokensPropertyEnabled() method is an [Experimental] opt-in to use
            // the new max_completion_tokens JSON property instead of the legacy max_tokens property.
            // This extension method will be removed and unnecessary in a future service API version;
            // please disable the [Experimental] warning to acknowledge.
#pragma warning disable AOAI001
            requestOptions.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

            // Start spinner and make async API call
            var spinnerTask = Task.Run(() => ShowSpinner());
            var apiTask = _client.CompleteChatAsync(_conversationHistory, requestOptions);
            
            var response = await apiTask;
            
            // Stop spinner
            _stopSpinner = true;
            await spinnerTask;
            
            // Handle tool calls if present
            if (response.Value.ToolCalls.Count > 0)
            {
                // Check if we've exceeded max tool calls
                if (_toolCallCount >= MAX_TOOL_CALLS_PER_MESSAGE)
                {
                    _conversationHistory.Add(new AssistantChatMessage("I've reached the maximum number of tool calls for this message. Let me provide a response with the information I have."));
                    return;
                }

                // Add assistant message with tool calls to history
                _conversationHistory.Add(new AssistantChatMessage(response.Value.ToolCalls));

                // Execute tool calls
                foreach (var toolCall in response.Value.ToolCalls)
                {
                    if (toolCall is ChatToolCall chatToolCall)
                    {
                        try
                        {
                            string toolResult = string.Empty;
                            
                            // Check if this is a fake tool first
                            var fakeTool = _fakeToolManager.GetFakeTool(chatToolCall.FunctionName);
                            if (fakeTool != null)
                            {
                                // Handle fake tool
                                var argumentsJson = chatToolCall.FunctionArguments.ToString();
                                
                                // Show fake tool invocation notification
                                AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Fake tool invoked: {chatToolCall.FunctionName}[/]");
                                AnsiConsole.MarkupLine($"[dim grey]   Parameters: {argumentsJson}[/]");
                                AnsiConsole.MarkupLine($"[dim grey]   → {fakeTool.Response}[/]");
                                
                                _conversationHistory.Add(new ToolChatMessage(chatToolCall.Id, fakeTool.Response));
                            }
                            else
                            {
                                // Handle MCP tools
                                var mcpTool = mcpTools.FirstOrDefault(t => t.Name == chatToolCall.FunctionName);
                                if (mcpTool != null)
                                {
                                    // Convert BinaryData to AIFunctionArguments
                                    var argumentsJson = chatToolCall.FunctionArguments.ToString();
                                    var arguments = new AIFunctionArguments();
                                    
                                    if (!string.IsNullOrEmpty(argumentsJson) && argumentsJson != "{}")
                                    {
                                        using var doc = JsonDocument.Parse(argumentsJson);
                                        foreach (var property in doc.RootElement.EnumerateObject())
                                        {
                                            arguments[property.Name] = property.Value.ToString();
                                        }
                                    }
                                    
                                    var result = await mcpTool.InvokeAsync(arguments);
                                    _conversationHistory.Add(new ToolChatMessage(chatToolCall.Id, result.ToString()));
                                    
                                    AnsiConsole.MarkupLine($"[dim grey]• calling {chatToolCall.FunctionName}[/]");
                                }
                            }
                            
                            // Increment tool call counter
                            _toolCallCount++;
                        }
                        catch (Exception ex)
                        {
                            _conversationHistory.Add(new ToolChatMessage(chatToolCall.Id, $"Error: {ex.Message}"));
                        }
                    }
                }

                // Get another response after tool execution
                await SendMessageInternalAsync();
            }
            else
            {
                var assistantMessage = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;

                if (!string.IsNullOrEmpty(assistantMessage))
                {
                    // Add assistant response to conversation history
                    _conversationHistory.Add(new AssistantChatMessage(assistantMessage));
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

    private static ChatTool ConvertAIFunctionToChatTool(AIFunction aiFunction)
    {
        // Extract parameters from the underlying method
        var methodParams = aiFunction.UnderlyingMethod?.GetParameters() ?? Array.Empty<System.Reflection.ParameterInfo>();
        
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        
        foreach (var param in methodParams)
        {
            properties[param.Name!] = new
            {
                type = GetJsonSchemaType(param.ParameterType),
                description = param.Name
            };
            
            // Add to required if not nullable and no default value
            if (!param.HasDefaultValue && !IsNullable(param.ParameterType))
            {
                required.Add(param.Name!);
            }
        }
        
        var parametersSchema = new
        {
            type = "object",
            properties = properties,
            required = required.ToArray()
        };
        
        var parameters = System.BinaryData.FromString(JsonSerializer.Serialize(parametersSchema));
        
        return ChatTool.CreateFunctionTool(
            functionName: aiFunction.Name,
            functionDescription: aiFunction.Description ?? "MCP function",
            functionParameters: parameters
        );
    }

    private static string GetJsonSchemaType(Type type)
    {
        // Handle nullable types
        if (IsNullable(type))
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
        }
        
        return type switch
        {
            Type t when t == typeof(string) => "string",
            Type t when t == typeof(int) || t == typeof(long) || t == typeof(short) => "integer",
            Type t when t == typeof(float) || t == typeof(double) || t == typeof(decimal) => "number",
            Type t when t == typeof(bool) => "boolean",
            Type t when t.IsArray || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) => "array",
            _ => "object"
        };
    }

    private static bool IsNullable(Type type)
    {
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);
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

            // Clear existing history except system message (keep the first message if it's SystemChatMessage)
            var systemMessage = _conversationHistory.FirstOrDefault(msg => msg is SystemChatMessage);
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
                        _conversationHistory.Add(new UserChatMessage(content));
                        break;
                    case "AssistantChatMessage":
                        _conversationHistory.Add(new AssistantChatMessage(content));
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

    private static string ExtractMessageContent(OpenAIChatMessage message)
    {
        return message switch
        {
            SystemChatMessage systemMsg => systemMsg.Content.FirstOrDefault()?.Text ?? "",
            UserChatMessage userMsg => userMsg.Content.FirstOrDefault()?.Text ?? "",
            AssistantChatMessage assistantMsg => assistantMsg.Content.FirstOrDefault()?.Text ?? "",
            _ => ""
        };
    }

    public void ClearConversationHistory()
    {
        // Keep only the system message (first message if it's a SystemChatMessage)
        var systemMessage = _conversationHistory.FirstOrDefault(msg => msg is SystemChatMessage);
        _conversationHistory.Clear();
        
        if (systemMessage != null)
        {
            _conversationHistory.Add(systemMessage);
        }
        
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Conversation history cleared[/]");
    }
}