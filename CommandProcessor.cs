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

        if (command == "/exit" || command == "/quit")
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

        var maxContextTokens = ResolveMaxContextTokens(config, providerName);
        _conversationManager.SwitchProvider(newClient, providerName, maxContextTokens);
    }

    private static int ResolveMaxContextTokens(Microsoft.Extensions.Configuration.IConfiguration config, string providerName)
    {
        // Check per-provider MaxContextTokens first
        var providers = config.GetSection("ChatProviders").GetChildren();
        foreach (var provider in providers)
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (int.TryParse(provider["MaxContextTokens"], out var providerTokens))
                    return providerTokens;
                break;
            }
        }
        // Fall back to top-level setting
        return int.TryParse(config["MaxContextTokens"], out var tokens) ? tokens : 128000;
    }

    private string? LaunchEditor()
    {
        var tmpFile = Path.GetTempFileName();
        try
        {
            var candidates = BuildEditorCandidates();
            Process? process = null;
            string? launchedEditor = null;

            foreach (var (editor, warning) in candidates)
            {
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = editor,
                        Arguments = tmpFile,
                        UseShellExecute = false
                    });
                    if (process != null)
                    {
                        launchedEditor = editor;
                        if (warning != null)
                            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{warning}[/]");
                        break;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // Editor not found on PATH — try the next candidate
                }
            }

            if (process == null || launchedEditor == null)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No editor found. Set the EDITOR environment variable to your preferred editor.[/]");
                if (OperatingSystem.IsWindows())
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Tip: `winget install Microsoft.Edit` for a console editor that works well with /edit.[/]");
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

    private static IEnumerable<(string Editor, string? Warning)> BuildEditorCandidates()
    {
        var envEditor = Environment.GetEnvironmentVariable("EDITOR");
        if (!string.IsNullOrWhiteSpace(envEditor))
            yield return (envEditor, null);

        if (OperatingSystem.IsWindows())
        {
            yield return ("edit", null);
            yield return ("notepad", "Falling back to notepad — if tabs are enabled, /edit may return immediately. Set EDITOR to a console editor (e.g. `edit`) for reliable behavior.");
        }
        else
        {
            yield return ("nano", null);
        }
    }

}
