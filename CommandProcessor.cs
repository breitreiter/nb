using System.Diagnostics;
using Spectre.Console;
using nb.Utilities;

namespace nb;

public enum CommandAction { Exit, Continue, AddToHistory, SendToLlm }

public class CommandResult
{
    public CommandAction Action { get; set; }
    public string? ModifiedInput { get; set; }

    public static CommandResult Exit() => new() { Action = CommandAction.Exit };
    public static CommandResult Continue() => new() { Action = CommandAction.Continue };
    public static CommandResult AddToHistory(string input) => new() { Action = CommandAction.AddToHistory, ModifiedInput = input };
    public static CommandResult SendToLlm(string input) => new() { Action = CommandAction.SendToLlm, ModifiedInput = input };
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

        if (command == "/edit")
        {
            var input = LaunchEditor();
            if (input != null)
                return CommandResult.SendToLlm(input);
            return CommandResult.Continue();
        }

        if (command == "/clear")
        {
            _conversationManager.ClearConversationHistory();
            return CommandResult.Continue();
        }

        if (command == "/provider")
        {
            HandleProviderCommand();
            return CommandResult.Continue();
        }

        // Not a command - return Continue so main loop can handle it
        return CommandResult.Continue();
    }

    private void HandleProviderCommand()
    {
        var config = _configService.GetConfiguration();
        var currentProvider = _conversationManager.GetCurrentProvider();
        var configured = _providerManager.GetConfiguredProviders(config).ToList();

        if (configured.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No providers configured[/]");
            return;
        }

        var choices = configured
            .Select(name => name.Equals(currentProvider, StringComparison.OrdinalIgnoreCase)
                ? $"{name} (active)"
                : name)
            .ToList();

        var selection = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[{UIColors.SpectreWarning}]Switch provider[/]")
                .HighlightStyle(UIColors.SpectreInfo)
                .AddChoices(choices));

        // Strip the "(active)" suffix if present
        var providerName = selection.Replace(" (active)", "");

        if (providerName.Equals(currentProvider, StringComparison.OrdinalIgnoreCase))
            return; // Already active, nothing to do

        var newClient = _providerManager.TryCreateChatClient(config, providerName);
        if (newClient == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to switch to provider '{providerName}'[/]");
            return;
        }

        _conversationManager.SwitchProvider(newClient, providerName);
    }

    private string? LaunchEditor()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var editor = Environment.GetEnvironmentVariable("EDITOR") ?? "nano";
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = editor,
                Arguments = tmpFile,
                UseShellExecute = false
            });

            if (process == null)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to launch editor: {editor}[/]");
                return null;
            }

            process.WaitForExit();

            var content = File.ReadAllText(tmpFile).Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Empty input, cancelled[/]");
                return null;
            }

            return content;
        }
        finally
        {
            try { File.Delete(tmpFile); } catch { }
        }
    }

}
