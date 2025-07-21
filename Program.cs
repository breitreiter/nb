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
    private static McpManager _mcpManager = new McpManager();
    private static ConversationManager _conversationManager;
    private static ConfigurationService _configurationService = new ConfigurationService();
    private static SemanticMemoryService _semanticMemoryService;
    private static CommandProcessor _commandProcessor;
    private static FileContentExtractor _fileExtractor;
    private static PromptProcessor _promptProcessor;

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

        // Initialize refactored services
        _fileExtractor = new FileContentExtractor();
        _promptProcessor = new PromptProcessor(_mcpManager);
        _commandProcessor = new CommandProcessor(_semanticMemoryService, _fileExtractor, _promptProcessor);

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

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            // Process command through the command processor
            var result = await _commandProcessor.ProcessCommandAsync(userInput);

            switch (result.Action)
            {
                case CommandAction.Exit:
                    return;
                
                case CommandAction.Continue:
                    continue;
                
                case CommandAction.SendToLlm:
                    await _conversationManager.SendMessageAsync(result.ModifiedInput ?? userInput);
                    break;
            }
        }
    }
}