using Spectre.Console;

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
    private readonly FileContentExtractor _fileExtractor;
    private readonly PromptProcessor _promptProcessor;
    private readonly ConversationManager _conversationManager;

    public CommandProcessor(FileContentExtractor fileExtractor, PromptProcessor promptProcessor, ConversationManager conversationManager)
    {
        _fileExtractor = fileExtractor;
        _promptProcessor = promptProcessor;
        _conversationManager = conversationManager;
    }

    public async Task<CommandResult> ProcessCommandAsync(string userInput)
    {
        var command = userInput.Trim().ToLowerInvariant();

        if (command == "exit")
        {
            return CommandResult.Exit();
        }


        if (command == "/clear")
        {
            return HandleClearCommand();
        }

        if (command.StartsWith("/insert "))
        {
            var result = await HandleInsertCommand(userInput);
            return result;
        }

        if (command == "/prompts")
        {
            _promptProcessor.DisplayAvailablePrompts();
            return CommandResult.Continue();
        }

        if (command.StartsWith("/prompt "))
        {
            var result = await HandlePromptCommand(userInput);
            return result;
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

    private async Task<CommandResult> HandleInsertCommand(string userInput)
    {
        var filePath = userInput.Substring(8).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a file path: /insert <filepath>[/]");
            return CommandResult.Continue();
        }

        if (filePath.StartsWith('\"') && filePath.EndsWith('\"'))
        {
            filePath = filePath[1..^1]; // Remove surrounding quotes
        }

        var fileContent = await _fileExtractor.ExtractFileContentAsync(filePath);
        if (!string.IsNullOrEmpty(fileContent))
        {
            var fileName = Path.GetFileName(filePath);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]File content from {fileName} added to conversation context[/]");
            var modifiedInput = $"Here is the content from file '{fileName}':\n\n{fileContent}";
            return CommandResult.AddToHistory(modifiedInput);
        }

        return CommandResult.Continue();
    }

    private async Task<CommandResult> HandlePromptCommand(string userInput)
    {
        var promptName = userInput.Substring(8).Trim();
        if (string.IsNullOrEmpty(promptName))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a prompt name: /prompt <name>[/]");
            return CommandResult.Continue();
        }

        var promptResult = await _promptProcessor.InvokePromptAsync(promptName);
        if (!string.IsNullOrEmpty(promptResult))
        {
            await _conversationManager.SendMessageAsync(promptResult);
        }

        return CommandResult.Continue();
    }


    private void DisplayHelp()
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Available commands:[/]");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]exit[/] - Quit the application");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/clear[/] - Clear conversation history");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/insert <filepath>[/] - Insert entire file content into conversation context (PDF or text)");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompts[/] - List available MCP prompts");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompt <name>[/] - Invoke an MCP prompt");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]?[/] - Show this help");
    }
}