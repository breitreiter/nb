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
    private readonly SemanticMemoryService _semanticMemoryService;
    private readonly FileContentExtractor _fileExtractor;
    private readonly PromptProcessor _promptProcessor;
    private readonly ConversationManager _conversationManager;

    public CommandProcessor(SemanticMemoryService semanticMemoryService, FileContentExtractor fileExtractor, PromptProcessor promptProcessor, ConversationManager conversationManager)
    {
        _semanticMemoryService = semanticMemoryService;
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

        if (command == "/pwd")
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Current directory: {Directory.GetCurrentDirectory()}[/]");
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

        if (command.StartsWith("/run "))
        {
            var result = await HandleRunCommand(userInput);
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

    private async Task HandleChangeDirectoryCommand(string userInput)
    {
        try
        {
            var path = userInput.Substring(4).Trim();
            if (string.IsNullOrEmpty(path))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a directory path: /cd <path>[/]");
                return;
            }

            Directory.SetCurrentDirectory(path);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Changed directory to: {Directory.GetCurrentDirectory()}[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error changing directory:[/] {ex.Message}");
        }
    }

    private async Task<CommandResult> HandleIndexCommand(string userInput)
    {
        var filePath = userInput.Substring(7).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a file path: /index <filepath>[/]");
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
            await _conversationManager.SendMessageAsync(systemMessage);
        }

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

    private async Task<CommandResult> HandleRunCommand(string userInput)
    {
        var filePath = userInput.Substring(5).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Please specify a file path: /run <filepath>[/]");
            return CommandResult.Continue();
        }

        if (filePath.StartsWith('\"') && filePath.EndsWith('\"'))
        {
            filePath = filePath[1..^1]; // Remove surrounding quotes
        }

        try
        {
            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]File not found: {filePath}[/]");
                return CommandResult.Continue();
            }

            var lines = await File.ReadAllLinesAsync(filePath);
            var errors = new List<string>();
            var processedCount = 0;
            var successCount = 0;

            AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]Executing commands from: {Path.GetFileName(filePath)}[/]");

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                {
                    continue; // Skip empty lines and comments
                }

                processedCount++;
                try
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]Line {i + 1}:[/] {line}");
                    var result = await ProcessCommandAsync(line);
                    
                    // Handle the result appropriately
                    if (result.Action == CommandAction.Exit)
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Exit command encountered on line {i + 1}, stopping execution[/]");
                        break;
                    }
                    else if (result.Action == CommandAction.AddToHistory)
                    {
                        var historyText = result.ModifiedInput ?? line;
                        _conversationManager.AddToConversationHistory(historyText);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]Added to conversation history[/]");
                    }
                    else if (result.Action == CommandAction.Continue && !line.StartsWith("/"))
                    {
                        // This is a non-command line (prompt/question) - send to LLM
                        await _conversationManager.SendMessageAsync(line);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]Sent to LLM[/]");
                    }
                    
                    successCount++;
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Line {i + 1}: {ex.Message}";
                    errors.Add(errorMsg);
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error on line {i + 1}:[/] {ex.Message}");
                }
            }

            // Display summary
            if (errors.Any())
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Execution complete: {successCount}/{processedCount} commands succeeded, {errors.Count} failed[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Execution complete: All {successCount} commands succeeded[/]");
            }

            return CommandResult.Continue();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error reading file:[/] {ex.Message}");
            return CommandResult.Continue();
        }
    }

    private void DisplayHelp()
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Available commands:[/]");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]exit[/] - Quit the application");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/pwd[/] - Show current working directory");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/cd <path>[/] - Change directory");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/index <filepath>[/] - Upload and process file for semantic search (PDF or text)");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/insert <filepath>[/] - Insert entire file content into conversation context (PDF or text)");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/run <filepath>[/] - Execute commands from a file (one command per line)");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompts[/] - List available MCP prompts");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompt <name>[/] - Invoke an MCP prompt");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]?[/] - Show this help");
    }
}