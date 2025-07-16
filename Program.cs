using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using ModelContextProtocol.Protocol;

namespace nb;


public class Program
{
    private static ChatClient _client;
    private static IMcpManager _mcpManager = new McpManager();
    private static IConversationManager _conversationManager;
    private static IConfigurationService _configurationService = new ConfigurationService();
    private static ISemanticMemoryService _semanticMemoryService;

    public static async Task Main(string[] args)
    {
        var config = _configurationService.GetConfiguration();
        InitializeOpenAIClient(config);
        await _mcpManager.InitializeAsync();
        
        // Initialize semantic memory service
        var endpoint = config["AzureOpenAI:Endpoint"];
        var apiKey = config["AzureOpenAI:ApiKey"];
        var chatDeployment = config["AzureOpenAI:ChatDeploymentName"] ?? "o4-mini";
        var embeddingDeployment = config["AzureOpenAI:EmbeddingDeploymentName"] ?? "text-embedding-3-small";
        var chunkSize = config.GetValue<int>("SemanticMemory:ChunkSize", 256);
        var chunkOverlap = config.GetValue<int>("SemanticMemory:ChunkOverlap", 64);
        var similarityThreshold = config.GetValue<double>("SemanticMemory:SimilarityThreshold", 0.7);
        _semanticMemoryService = new SemanticMemoryService(endpoint, apiKey, embeddingDeployment, chunkSize, chunkOverlap, similarityThreshold);
        await _semanticMemoryService.InitializeAsync();
        
        // Initialize conversation manager with dependencies
        _conversationManager = new ConversationManager(_client, _mcpManager);
        _conversationManager.InitializeWithSystemPrompt(_configurationService.GetSystemPrompt());
        _conversationManager.SetSemanticMemoryService(_semanticMemoryService);

        var initialPrompt = string.Join(" ", args);

        if (!string.IsNullOrEmpty(initialPrompt))
        {
            await _conversationManager.SendMessageAsync(initialPrompt);
        }

        await StartChatLoop();
        
        // Cleanup MCP clients
        _mcpManager.Dispose();
    }

    private static void InitializeOpenAIClient(IConfiguration config)
    {
        var endpoint = config["AzureOpenAI:Endpoint"];
        var apiKey = config["AzureOpenAI:ApiKey"];
        var deployment = config["AzureOpenAI:ChatDeploymentName"] ?? "o4-mini";

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error: Azure OpenAI endpoint and API key must be configured in appsettings.json[/]");
            Environment.Exit(1);
        }

        var endpointUri = new Uri(endpoint);

        AzureOpenAIClientOptions options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        AzureOpenAIClient azureClient = new(
            endpointUri,
            new AzureKeyCredential(apiKey), 
            options);
        _client = azureClient.GetChatClient(deployment);
    }


    private static async Task StartChatLoop()
    {
        AnsiConsole.MarkupLine("[white]N[/]ota[white]B[/]ene 0.3α [grey]▪[/] [cadetblue_1]exit[/] [grey]to quit[/] [cadetblue_1]?[/] [grey]for help[/]");

        while (true)
        {
            // Show prompt and capture input manually
            AnsiConsole.Markup("[yellow]You:[/] ");
            var userInput = Console.ReadLine();
            
            // Clear the input line by moving cursor up and clearing
            Console.SetCursorPosition(0, Console.CursorTop - 1);
            Console.Write(new string(' ', Console.WindowWidth));
            Console.SetCursorPosition(0, Console.CursorTop);
            
            // Display the user's prompt in a Spectre panel
            var panel = new Panel(userInput ?? "")
                .Header("[yellow]You[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(new Style(Color.Yellow));
            
            AnsiConsole.Write(panel);

            var command = userInput?.ToLower();

            if (command == "exit")
                break;
            else if (command == "/pwd")
            {
                AnsiConsole.MarkupLine($"[green]Current directory:[/] {Directory.GetCurrentDirectory()}");
                continue;
            }
            else if (command?.StartsWith("/cd ") == true)
            {
                var path = userInput.Substring(4).Trim();
                try
                {
                    Directory.SetCurrentDirectory(path);
                    AnsiConsole.MarkupLine($"[green]Changed directory to:[/] {Directory.GetCurrentDirectory()}");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error changing directory:[/] {ex.Message}");
                }
                continue;
            }
            else if (command?.StartsWith("/upload ") == true)
            {
                var filePath = userInput.Substring(8).Trim();
                if (string.IsNullOrEmpty(filePath))
                {
                    AnsiConsole.MarkupLine("[red]Please specify a file path: /upload <filepath>[/]");
                }
                else
                {
                    await _semanticMemoryService.UploadFileAsync(filePath);
                }
                continue;
            }
            else if (command == "/prompts")
            {
                var prompts = _mcpManager.GetPrompts();
                if (!prompts.Any())
                {
                    AnsiConsole.MarkupLine("[yellow]No prompts available from connected MCP servers[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Available MCP prompts ({prompts.Count}):[/]");
                    foreach (var prompt in prompts)
                    {
                        var args = prompt.ProtocolPrompt.Arguments?.Count > 0 
                            ? $" ({prompt.ProtocolPrompt.Arguments.Count} args)" 
                            : "";
                        AnsiConsole.MarkupLine($"  [white]{prompt.Name}[/]{args} - {prompt.Description ?? "No description"}");
                    }
                    AnsiConsole.MarkupLine("[dim]Use '/prompt <name>' to invoke a specific prompt[/]");
                }
                continue;
            }
            else if (command?.StartsWith("/prompt ") == true)
            {
                var promptName = userInput.Substring(8).Trim();
                if (string.IsNullOrEmpty(promptName))
                {
                    AnsiConsole.MarkupLine("[red]Please specify a prompt name: /prompt <name>[/]");
                    AnsiConsole.MarkupLine("[dim]Use '/prompts' to see available prompts[/]");
                }
                else
                {
                    await InvokePromptAsync(promptName);
                }
                continue;
            }
            else if (command == "?")
            {
                AnsiConsole.MarkupLine("[yellow]Available commands:[/]");
                AnsiConsole.MarkupLine("  [white]exit[/] - Quit the application");
                AnsiConsole.MarkupLine("  [white]/pwd[/] - Show current working directory");
                AnsiConsole.MarkupLine("  [white]/cd <path>[/] - Change directory");
                AnsiConsole.MarkupLine("  [white]/upload <filepath>[/] - Upload and process file for semantic search (PDF, TXT, MD)");
                AnsiConsole.MarkupLine("  [white]/prompts[/] - List available MCP prompts");
                AnsiConsole.MarkupLine("  [white]/prompt <name>[/] - Invoke an MCP prompt");
                AnsiConsole.MarkupLine("  [white]?[/] - Show this help");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                await _conversationManager.SendMessageAsync(userInput);
            }
        }
    }

    private static async Task InvokePromptAsync(string promptName)
    {
        try
        {
            var prompts = _mcpManager.GetPrompts();
            var prompt = prompts.FirstOrDefault(p => p.Name.Equals(promptName, StringComparison.OrdinalIgnoreCase));
            
            if (prompt == null)
            {
                AnsiConsole.MarkupLine($"[red]Prompt '{promptName}' not found[/]");
                AnsiConsole.MarkupLine("[dim]Use '/prompts' to see available prompts[/]");
                return;
            }

            // Collect arguments if the prompt requires them
            var arguments = new Dictionary<string, object>();
            if (prompt.ProtocolPrompt.Arguments?.Count > 0)
            {
                foreach (var arg in prompt.ProtocolPrompt.Arguments)
                {
                    var description = string.IsNullOrEmpty(arg.Description) ? "no description" : arg.Description;
                    var value = AnsiConsole.Ask<string>($"[white]{arg.Name}[/] ({description}): ");
                    arguments[arg.Name] = value;
                }
            }

            // Invoke the prompt
            AnsiConsole.MarkupLine($"[yellow]Invoking prompt '{promptName}'...[/]");
            var result = await prompt.GetAsync(arguments);
            
            // Send the result to the conversation
            if (result.Messages?.Count > 0)
            {
                AnsiConsole.MarkupLine($"[green]Prompt result received, sending to AI...[/]");

                var textBlocks = new List<string>();

                foreach (var message in result.Messages)
                {
                    if (message.Content is TextContentBlock textBlock)
                    {
                        textBlocks.Add(textBlock.Text);
                    }
                }

                var combinedContent = string.Join("\n\n", textBlocks);
                               
                if (!string.IsNullOrEmpty(combinedContent))
                {
                    await _conversationManager.SendMessageAsync(combinedContent);
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]Prompt messages contained no text content[/]");
                }
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Prompt returned no messages[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error invoking prompt: {ex.Message}[/]");
        }
    }
}