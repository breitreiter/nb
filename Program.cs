using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using ModelContextProtocol.Protocol;

namespace nb;


public class Program
{
    private static IChatClient _client;
    private static McpManager _mcpManager = new McpManager();
    private static FakeToolManager _fakeToolManager = new FakeToolManager();
    private static ConversationManager _conversationManager;
    private static ConfigurationService _configurationService = new ConfigurationService();
    private static ProviderManager _providerManager = new ProviderManager();
    private static CommandProcessor _commandProcessor;
    private static FileContentExtractor _fileExtractor;
    private static PromptProcessor _promptProcessor;

    public static async Task Main(string[] args)
    {
        var config = _configurationService.GetConfiguration();
        
        // Initialize chat client using provider system
        _client = _providerManager.TryCreateChatClient(config);
        if (_client == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to initialize chat client. Please check your configuration.[/]");
            _providerManager.ShowProviderStatus(config);
            Environment.Exit(1);
        }
        
        // Determine execution mode first to control banner display
        var isInteractiveMode = args.Length == 0;
        await _mcpManager.InitializeAsync(showBanners: isInteractiveMode);
        
        // Load fake tools (notifications will be shown after integration)
        var fakeToolResult = await _fakeToolManager.LoadFakeToolsAsync();
        
        // Perform integration to determine overrides
        if (fakeToolResult.Success && fakeToolResult.ToolsLoaded > 0)
        {
            var mcpTools = _mcpManager.GetTools();
            _fakeToolManager.IntegrateWithMcpTools(mcpTools);
            
            if (isInteractiveMode)
            {
                var overriddenTools = _fakeToolManager.GetOverriddenTools();
                var overrideCount = overriddenTools.Count;
                var newCount = fakeToolResult.ToolsLoaded - overrideCount;
                
                if (overrideCount > 0)
                {
                    foreach (var toolName in overriddenTools)
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Fake tool '{toolName}' overrides MCP tool[/]");
                    }
                }
                
                AnsiConsole.MarkupLine($"[{UIColors.SpectreFakeTool}]🎭 Loaded {fakeToolResult.ToolsLoaded} fake tools ({overrideCount} override{(overrideCount == 1 ? "" : "s")}, {newCount} new)[/]");
            }
        }
        
        // Initialize conversation manager with dependencies
        _conversationManager = new ConversationManager(_client, _mcpManager, _fakeToolManager);
        _conversationManager.InitializeWithSystemPrompt(_configurationService.GetSystemPrompt());
        
        // Load conversation history from previous sessions
        await _conversationManager.LoadConversationHistoryAsync();

        // Initialize refactored services
        _fileExtractor = new FileContentExtractor();
        _promptProcessor = new PromptProcessor(_mcpManager);
        _commandProcessor = new CommandProcessor(_fileExtractor, _promptProcessor, _conversationManager);

        // Execute based on mode
        if (args.Length > 0)
        {
            // Single-shot mode: execute command and exit
            var userInput = string.Join(" ", args);
            await ExecuteSingleCommand(userInput);
        }
        else
        {
            // Interactive mode: start chat loop
            await StartChatLoop();
        }
        
        // Save conversation history before exit
        await _conversationManager.SaveConversationHistoryAsync();
        
        // Cleanup MCP clients
        _mcpManager.Dispose();
    }



    private static async Task StartChatLoop()
    {
        AnsiConsole.MarkupLine("[white]N[/]ota[white]B[/]ene 0.3α [grey]▪[/] [cadetblue_1]exit[/] [grey]to quit[/] [cadetblue_1]?[/] [grey]for help[/]");
        
        // Show directory context banner
        var currentDir = Directory.GetCurrentDirectory();
        var dirName = Path.GetFileName(currentDir);
        var historyExists = File.Exists(".nb_conversation_history.json");
        
        if (historyExists)
        {
            AnsiConsole.MarkupLine($"[dim grey]Loaded conversation history for directory:[/] [yellow]{dirName}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim grey]Starting fresh conversation for directory:[/] [yellow]{dirName}[/]");
        }

        while (true)
        {
            // Add visual separator before user input
            string divider = string.Concat(Enumerable.Repeat("🞌", Console.WindowWidth));
            Console.WriteLine($"{UIColors.NativeMuted}{divider}{UIColors.NativeReset}");

            AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]You:[/] ");
            Console.Write(UIColors.NativeUserInput);
            var userInput = Console.ReadLine();
            Console.Write(UIColors.NativeReset);
            
            // Add visual separator after user input
            Console.WriteLine($"{UIColors.NativeMuted}{divider}{UIColors.NativeReset}");

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            // Process command through the command processor
            var result = await _commandProcessor.ProcessCommandAsync(userInput);

            switch (result.Action)
            {
                case CommandAction.Exit:
                    return;
                
                case CommandAction.Continue:
                    // Check if this was a non-command that should go to LLM
                    if (!userInput.TrimStart().StartsWith("/") && userInput.Trim() != "?" && userInput.Trim() != "exit")
                    {
                        await _conversationManager.SendMessageAsync(userInput);
                    }
                    break;
                
                case CommandAction.AddToHistory:
                    _conversationManager.AddToConversationHistory(result.ModifiedInput ?? userInput);
                    break;
            }
        }
    }

    private static async Task ExecuteSingleCommand(string userInput)
    {
        // Process command through the command processor (same logic as interactive mode)
        var result = await _commandProcessor.ProcessCommandAsync(userInput);

        switch (result.Action)
        {
            case CommandAction.Exit:
                // Exit command processed, just return
                return;
            
            case CommandAction.Continue:
                // Check if this was a non-command that should go to LLM
                if (!userInput.TrimStart().StartsWith("/") && userInput.Trim() != "?" && userInput.Trim() != "exit")
                {
                    await _conversationManager.SendMessageAsync(userInput);
                }
                break;
            
            case CommandAction.AddToHistory:
                // Maintain conversation history just like interactive mode
                _conversationManager.AddToConversationHistory(result.ModifiedInput ?? userInput);
                break;
        }
    }
}