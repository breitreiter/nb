using Spectre.Console;

namespace nb;

public enum CommandAction { Exit, Continue, SendToLlm, AddToHistory }

public class CommandResult
{
    public CommandAction Action { get; set; }
    public string? ModifiedInput { get; set; }

    public static CommandResult Exit() => new() { Action = CommandAction.Exit };
    public static CommandResult Continue() => new() { Action = CommandAction.Continue };
    public static CommandResult SendToLlm(string input) => new() { Action = CommandAction.SendToLlm, ModifiedInput = input };
    public static CommandResult AddToHistory(string input) => new() { Action = CommandAction.AddToHistory, ModifiedInput = input };
}

public class CommandProcessor
{
    private readonly SemanticMemoryService _semanticMemoryService;
    private readonly FileContentExtractor _fileExtractor;
    private readonly PromptProcessor _promptProcessor;

    public CommandProcessor(SemanticMemoryService semanticMemoryService, FileContentExtractor fileExtractor, PromptProcessor promptProcessor)
    {
        _semanticMemoryService = semanticMemoryService;
        _fileExtractor = fileExtractor;
        _promptProcessor = promptProcessor;
    }

    public async Task<CommandResult> ProcessCommandAsync(string userInput)
    {
        var command = userInput.Trim().ToLowerInvariant();

        if (command == "exit")
        {
            return CommandResult.Exit();
        }

        if (command == "/pwd")
        {
            AnsiConsole.MarkupLine($"[green]Current directory: {Directory.GetCurrentDirectory()}[/]");
            return CommandResult.Continue();
        }

        if (command.StartsWith("/cd "))
        {
            await HandleChangeDirectoryCommand(userInput);
            return CommandResult.Continue();
        }

        if (command.StartsWith("/index "))
        {
            return await HandleIndexCommand(userInput);
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

        // Not a command - pass to LLM
        return CommandResult.SendToLlm(userInput);
    }

    private async Task HandleChangeDirectoryCommand(string userInput)
    {
        try
        {
            var path = userInput.Substring(4).Trim();
            if (string.IsNullOrEmpty(path))
            {
                AnsiConsole.MarkupLine("[red]Please specify a directory path: /cd <path>[/]");
                return;
            }

            Directory.SetCurrentDirectory(path);
            AnsiConsole.MarkupLine($"[green]Changed directory to: {Directory.GetCurrentDirectory()}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error changing directory:[/] {ex.Message}");
        }
    }

    private async Task<CommandResult> HandleIndexCommand(string userInput)
    {
        var filePath = userInput.Substring(7).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine("[red]Please specify a file path: /index <filepath>[/]");
            return CommandResult.Continue();
        }

        if (filePath.StartsWith('\"') && filePath.EndsWith('\"'))
        {
            filePath = filePath[1..^1]; // Remove surrounding quotes
        }

        var success = await _semanticMemoryService.UploadFileAsync(filePath);
        if (success)
        {
            var fileName = Path.GetFileName(filePath);
            var systemMessage = $"SYSTEM: The document '{fileName}' has been successfully indexed and is now available for semantic search. You can search through this document's content using the search_documents tool when relevant to answer user questions.";
            return CommandResult.SendToLlm(systemMessage);
        }

        return CommandResult.Continue();
    }

    private async Task<CommandResult> HandleInsertCommand(string userInput)
    {
        var filePath = userInput.Substring(8).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine("[red]Please specify a file path: /insert <filepath>[/]");
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
            AnsiConsole.MarkupLine($"[green]File content from {fileName} added to conversation context[/]");
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
            AnsiConsole.MarkupLine("[red]Please specify a prompt name: /prompt <name>[/]");
            return CommandResult.Continue();
        }

        var promptResult = await _promptProcessor.InvokePromptAsync(promptName);
        if (!string.IsNullOrEmpty(promptResult))
        {
            return CommandResult.SendToLlm(promptResult);
        }

        return CommandResult.Continue();
    }

    private void DisplayHelp()
    {
        AnsiConsole.MarkupLine("[yellow]Available commands:[/]");
        AnsiConsole.MarkupLine("  [white]exit[/] - Quit the application");
        AnsiConsole.MarkupLine("  [white]/pwd[/] - Show current working directory");
        AnsiConsole.MarkupLine("  [white]/cd <path>[/] - Change directory");
        AnsiConsole.MarkupLine("  [white]/index <filepath>[/] - Upload and process file for semantic search (PDF or text)");
        AnsiConsole.MarkupLine("  [white]/insert <filepath>[/] - Insert entire file content into conversation context (PDF or text)");
        AnsiConsole.MarkupLine("  [white]/prompts[/] - List available MCP prompts");
        AnsiConsole.MarkupLine("  [white]/prompt <name>[/] - Invoke an MCP prompt");
        AnsiConsole.MarkupLine("  [white]?[/] - Show this help");
    }
}