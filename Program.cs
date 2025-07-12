using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace nb;


public class Program
{
    private static ChatClient _client;
    private static IMcpManager _mcpManager = new McpManager();
    private static IConversationManager _conversationManager;
    private static IConfigurationService _configurationService = new ConfigurationService();

    public static async Task Main(string[] args)
    {
        var config = _configurationService.GetConfiguration();
        InitializeOpenAIClient(config);
        await _mcpManager.InitializeAsync();
        
        // Initialize conversation manager with dependencies
        _conversationManager = new ConversationManager(_client, _mcpManager);
        _conversationManager.InitializeWithSystemPrompt(_configurationService.GetSystemPrompt());

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
        var deployment = config["AzureOpenAI:DeploymentName"] ?? "o4-mini";

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
        AnsiConsole.MarkupLine("[white]N[/]ota[white]B[/]ene 0.2α [grey]▪[/] [cadetblue_1]exit[/] [grey]to quit[/] [cadetblue_1]?[/] [grey]for help[/]");

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
            else if (command == "pwd")
            {
                AnsiConsole.MarkupLine($"[green]Current directory:[/] {Directory.GetCurrentDirectory()}");
                continue;
            }
            else if (command?.StartsWith("cd ") == true)
            {
                var path = userInput.Substring(3).Trim();
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
            else if (command == "?")
            {
                AnsiConsole.MarkupLine("[yellow]Available commands:[/]");
                AnsiConsole.MarkupLine("  [white]exit[/] - Quit the application");
                AnsiConsole.MarkupLine("  [white]pwd[/] - Show current working directory");
                AnsiConsole.MarkupLine("  [white]cd <path>[/] - Change directory");
                AnsiConsole.MarkupLine("  [white]?[/] - Show this help");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                await _conversationManager.SendMessageAsync(userInput);
            }
        }
    }
}