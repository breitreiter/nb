using Spectre.Console;
using nb.MCP;
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

        var fileName = Path.GetFileName(filePath);
        
        // Handle images separately
        if (_fileExtractor.IsImageFile(filePath))
        {
            try
            {
                var (description, imageData) = await _fileExtractor.ExtractImageAsync(filePath);
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Image from {fileName} added to conversation context[/]");
                _conversationManager.AddImageToConversationHistory(description, imageData, GetImageMimeType(filePath));
                return CommandResult.Continue();
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to load image: {ex.Message}[/]");
                return CommandResult.Continue();
            }
        }
        
        // Handle text/PDF files
        var fileContent = await _fileExtractor.ExtractFileContentAsync(filePath);
        if (!string.IsNullOrEmpty(fileContent))
        {
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
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/insert <filepath>[/] - Insert file content into conversation context (PDF, text, or images: JPG, PNG)");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompts[/] - List available MCP prompts");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]/prompt <name>[/] - Invoke an MCP prompt");
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreInfo}]?[/] - Show this help");
    }
    
    private string GetImageMimeType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };
    }
}