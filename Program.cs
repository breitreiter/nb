using Microsoft.Extensions.AI;
using Spectre.Console;
using nb.Providers;
using nb.MCP;
using nb.Shell;
using nb.Utilities;

namespace nb;


public class Program
{
    private static IChatClient _client = null!;
    private static McpManager _mcpManager = new McpManager();
    private static FakeToolManager _fakeToolManager = new FakeToolManager();
    private static ConversationManager _conversationManager = null!;
    private static ConfigurationService _configurationService = new ConfigurationService();
    private static ProviderManager _providerManager = new ProviderManager();
    private static CommandProcessor _commandProcessor = null!;
    private static FileContentExtractor _fileExtractor = null!;
    private static PromptProcessor _promptProcessor = null!;
    private static ShellEnvironment _shellEnvironment = null!;
    private static BashTool _bashTool = null!;
    private static WriteFileTool _writeFileTool = null!;
    private static ApprovalPatterns _approvalPatterns = new ApprovalPatterns();
    private static string? _systemPromptOverride = null;
    private static bool _noBash = false;

    private static string BuildUserInput(string[] args, string? stdinContent)
    {
        // Treat piped stdin as content to include in the message
        // Args (if present) become the instruction for what to do with the content

        if (!string.IsNullOrWhiteSpace(stdinContent))
        {
            var content = stdinContent.TrimEnd();

            // If we have args, format as: <content>\n\n<instruction>
            if (args.Length > 0)
            {
                var instruction = string.Join(" ", args);
                return $"```\n{content}\n```\n\n{instruction}";
            }
            // If no args, just send the content wrapped in code block
            else
            {
                return $"```\n{content}\n```";
            }
        }
        else
        {
            // No stdin, just use args as normal
            return string.Join(" ", args);
        }
    }

    private static string LoadSystemPrompt()
    {
        // If --system flag was provided, load from that path
        if (!string.IsNullOrEmpty(_systemPromptOverride))
        {
            if (!File.Exists(_systemPromptOverride))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: System prompt file not found: {Markup.Escape(_systemPromptOverride)}[/]");
                Environment.Exit(1);
            }
            return File.ReadAllText(_systemPromptOverride);
        }

        // Otherwise use default from ConfigurationService
        return _configurationService.GetSystemPrompt();
    }

    private static string[] ParseFlags(string[] args)
    {
        var remainingArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--approve" && i + 1 < args.Length)
            {
                _approvalPatterns.Add(args[++i]);
            }
            else if (args[i] == "--system" && i + 1 < args.Length)
            {
                _systemPromptOverride = args[++i];
            }
            else if (args[i] == "--nobash")
            {
                _noBash = true;
            }
            else
            {
                remainingArgs.Add(args[i]);
            }
        }

        return remainingArgs.ToArray();
    }

    public static async Task Main(string[] args)
    {
        // Parse flags (--approve, --system) before processing other args
        var remainingArgs = ParseFlags(args);

        var config = _configurationService.GetConfiguration();

        // Load theme
        UIColors.LoadTheme();

        // Initialize shell environment (unless --nobash)
        _shellEnvironment = ShellEnvironment.Detect();
        if (!_noBash)
        {
            _bashTool = new BashTool(_shellEnvironment);
            _writeFileTool = new WriteFileTool(_shellEnvironment);
        }

        // Initialize chat client using provider system
        var activeProviderName = config["ActiveProvider"] ?? string.Empty;
        _client = _providerManager.TryCreateChatClient(config)!;
        if (_client == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to initialize chat client. Please check your configuration.[/]");
            _providerManager.ShowProviderStatus(config);
            Environment.Exit(1);
        }

        // Determine execution mode first to control banner display
        var isInteractiveMode = remainingArgs.Length == 0;
        await _mcpManager.InitializeAsync(showBanners: isInteractiveMode);

        // Load fake tools (notifications will be shown after integration)
        var fakeToolResult = await _fakeToolManager.LoadFakeToolsAsync();

        // Perform integration to determine overrides
        if (fakeToolResult.Success && fakeToolResult.ToolsLoaded > 0)
        {
            var mcpTools = _mcpManager.GetTools();
            _fakeToolManager.IntegrateWithMcpTools(mcpTools);
        }

        // Initialize conversation manager with dependencies
        _conversationManager = new ConversationManager(
            _client, _mcpManager, _fakeToolManager, _bashTool, _writeFileTool, _approvalPatterns, activeProviderName);

        // Build enhanced system prompt with environment context (skip shell section if --nobash)
        var basePrompt = LoadSystemPrompt();
        var fullPrompt = _noBash
            ? basePrompt
            : $"{basePrompt}\n\n{_shellEnvironment.BuildSystemPromptSection()}";
        _conversationManager.InitializeWithSystemPrompt(fullPrompt);

        // Load conversation history from previous sessions
        await _conversationManager.LoadConversationHistoryAsync();

        // Initialize refactored services
        _fileExtractor = new FileContentExtractor();
        _promptProcessor = new PromptProcessor(_mcpManager);
        _commandProcessor = new CommandProcessor(_fileExtractor, _promptProcessor, _conversationManager, _configurationService, _providerManager);

        // Check if stdin is being piped
        string? stdinContent = null;
        if (Console.IsInputRedirected)
        {
            stdinContent = await Console.In.ReadToEndAsync();
        }

        // Execute based on mode
        if (remainingArgs.Length > 0 || stdinContent != null)
        {
            // Single-shot mode: execute command and exit
            var userInput = BuildUserInput(remainingArgs, stdinContent);
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
        // Get configured providers and current provider
        var config = _configurationService.GetConfiguration();
        var configuredProviders = _providerManager.GetConfiguredProviders(config);
        var currentProvider = _conversationManager.GetCurrentProvider();
        var providersList = string.Join(", ", configuredProviders.Select(p =>
            p == currentProvider ? $"[{UIColors.SpectreInfo}]{p}[/]" : $"[{UIColors.SpectreMuted}]{p}[/]"));

        // Get connected MCP servers
        var mcpServers = _mcpManager.GetConnectedServerNames();
        var mcpList = mcpServers.Count > 0
            ? string.Join(", ", mcpServers.Select(s => $"[{UIColors.SpectreMuted}]{s}[/]"))
            : "[dim]none[/]";

        AnsiConsole.MarkupLine(" " + UIColors.robot_img_1 + $"  [{UIColors.SpectreMuted}]AI: [/]{providersList}");
        AnsiConsole.MarkupLine(" " + UIColors.robot_img_2 + $"  [{UIColors.SpectreMuted}]MCP: [/]{mcpList}");
        AnsiConsole.MarkupLine(" " + UIColors.robot_img_3 + $"  NotaBene 0.9.1β [{UIColors.SpectreMuted}]▪[/] [{UIColors.SpectreAccent}]exit[/] [{UIColors.SpectreMuted}]to quit[/] [{UIColors.SpectreAccent}]?[/] [{UIColors.SpectreMuted}]for help[/]");
        
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