using Spectre.Console;
using nb.Utilities;

namespace nb;

public enum CommandAction { Exit, Continue, AddToHistory }

public class CommandResult
{
    public CommandAction Action { get; set; }
    public string? ModifiedInput { get; set; }

    public static CommandResult Exit() => new() { Action = CommandAction.Exit };
    public static CommandResult Continue() => new() { Action = CommandAction.Continue };
    public static CommandResult AddToHistory(string input) => new() { Action = CommandAction.AddToHistory, ModifiedInput = input };
}

public class CommandProcessor
{
    private readonly ConversationManager _conversationManager;
    private readonly ConfigurationService _configService;
    private readonly Providers.ProviderManager _providerManager;

    public CommandProcessor(ConversationManager conversationManager, ConfigurationService configService, Providers.ProviderManager providerManager)
    {
        _conversationManager = conversationManager;
        _configService = configService;
        _providerManager = providerManager;
    }

    public CommandResult ProcessCommand(string userInput)
    {
        var command = userInput.Trim().ToLowerInvariant();

        if (command == "exit" || command == "/quit")
        {
            return CommandResult.Exit();
        }

        if (command == "/clear")
        {
            return HandleClearCommand();
        }

        if (command == "/providers")
        {
            HandleProvidersCommand();
            return CommandResult.Continue();
        }

        if (command.StartsWith("/provider "))
        {
            HandleSwitchProviderCommand(userInput);
            return CommandResult.Continue();
        }

        if (command == "?")
        {
            DisplayHelp();
            return CommandResult.Continue();
        }

        // Not a command - return Continue so main loop can handle it
        return CommandResult.Continue();
    }


    private CommandResult HandleClearCommand()
    {
        _conversationManager.ClearConversationHistory();
        return CommandResult.Continue();
    }

    private void HandleProvidersCommand()
    {
        var config = _configService.GetConfiguration();
        var currentProvider = _conversationManager.GetCurrentProvider();
        _providerManager.ShowProvidersWithStatus(config, currentProvider);
    }

    private void HandleSwitchProviderCommand(string userInput)
    {
        var providerName = userInput.Substring(10).Trim();
        if (string.IsNullOrEmpty(providerName))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a provider name: /provider <name>[/]");
            return;
        }

        var config = _configService.GetConfiguration();
        var newClient = _providerManager.TryCreateChatClient(config, providerName);

        if (newClient == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to switch to provider '{providerName}'[/]");
            return;
        }

        _conversationManager.SwitchProvider(newClient, providerName);
    }

    private void DisplayHelp()
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Available commands:[/]");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]exit[/] - Quit the application");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/clear[/] - Clear conversation history");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/providers[/] - List all available providers");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/provider <name>[/] - Switch to a different provider");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]?[/] - Show this help");
    }
}
