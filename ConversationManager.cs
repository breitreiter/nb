using System.Text.Json;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Spectre.Console;
using OpenAIChatMessage = OpenAI.Chat.ChatMessage;

namespace nb;

public interface IConversationManager
{
    Task SendMessageAsync(string userMessage);
    void InitializeWithSystemPrompt(string systemPrompt);
}

public class ConversationManager : IConversationManager
{
    private readonly ChatClient _client;
    private readonly IMcpManager _mcpManager;
    private readonly List<OpenAIChatMessage> _conversationHistory = new();
    private bool _stopSpinner = false;

    public ConversationManager(ChatClient client, IMcpManager mcpManager)
    {
        _client = client;
        _mcpManager = mcpManager;
    }

    public void InitializeWithSystemPrompt(string systemPrompt)
    {
        _conversationHistory.Add(new SystemChatMessage(systemPrompt));
    }

    public async Task SendMessageAsync(string userMessage)
    {
        if (_client == null) return;

        // Add user message to conversation history
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

            // Add MCP tools if available
            var mcpTools = _mcpManager.GetTools();
            if (mcpTools.Count > 0)
            {
                foreach (var tool in mcpTools)
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
                // Add assistant message with tool calls to history
                _conversationHistory.Add(new AssistantChatMessage(response.Value.ToolCalls));

                // Execute tool calls
                foreach (var toolCall in response.Value.ToolCalls)
                {
                    if (toolCall is ChatToolCall chatToolCall)
                    {
                        try
                        {
                            // Find the MCP tool
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
                                
                                var toolResult = await mcpTool.InvokeAsync(arguments);
                                _conversationHistory.Add(new ToolChatMessage(chatToolCall.Id, toolResult.ToString()));
                                
                                // Stop spinner temporarily to show tool execution feedback
                                _stopSpinner = true;
                                await spinnerTask;
                                
                                AnsiConsole.MarkupLine($"[dim grey]• calling {chatToolCall.FunctionName}[/]");
                                
                                // Restart spinner for next tool or final response
                                spinnerTask = Task.Run(() => ShowSpinner());
                                _stopSpinner = false;
                            }
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
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
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
        var spinnerChars = new char[] { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };
        int index = 0;
        
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

    private static void RenderMarkdown(string markdown)
    {
        AnsiConsole.WriteLine(markdown);
    }
}