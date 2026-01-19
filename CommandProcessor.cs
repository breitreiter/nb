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
    private readonly ConfigurationService _configService;
    private readonly Providers.ProviderManager _providerManager;

    public CommandProcessor(FileContentExtractor fileExtractor, PromptProcessor promptProcessor, ConversationManager conversationManager, ConfigurationService configService, Providers.ProviderManager providerManager)
    {
        _fileExtractor = fileExtractor;
        _promptProcessor = promptProcessor;
        _conversationManager = conversationManager;
        _configService = configService;
        _providerManager = providerManager;
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

    private async Task<CommandResult> HandleInsertCommand(string userInput)
    {
        var filePath = userInput.Substring(8).Trim();
        if (string.IsNullOrEmpty(filePath))
        {
            Console.WriteLine("Please specify a file path: /insert <filepath>");
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
                Console.WriteLine($"Image from {fileName} added to conversation context");
                _conversationManager.AddImageToConversationHistory(description, imageData, GetImageMimeType(filePath));
                return CommandResult.Continue();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load image: {ex.Message}");
                return CommandResult.Continue();
            }
        }
        
        // Handle text/PDF files
        var fileContent = await _fileExtractor.ExtractFileContentAsync(filePath);
        if (!string.IsNullOrEmpty(fileContent))
        {
            Console.WriteLine($"File content from {fileName} added to conversation context");
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
            Console.WriteLine("Please specify a prompt name: /prompt <name>");
            return CommandResult.Continue();
        }

        var promptResult = await _promptProcessor.InvokePromptAsync(promptName);
        if (!string.IsNullOrEmpty(promptResult))
        {
            await _conversationManager.SendMessageAsync(promptResult);
        }

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
            Console.WriteLine("Please specify a provider name: /provider <name>");
            return;
        }

        var config = _configService.GetConfiguration();
        var newClient = _providerManager.TryCreateChatClient(config, providerName);

        if (newClient == null)
        {
            Console.WriteLine($"Failed to switch to provider '{providerName}'");
            return;
        }

        _conversationManager.SwitchProvider(newClient, providerName);
    }

    private void DisplayHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  exit                  - Quit the application");
        Console.WriteLine("  /clear                - Clear conversation history");
        Console.WriteLine("  /insert <filepath>    - Insert file content (PDF, text, images)");
        Console.WriteLine("  /prompts              - List available MCP prompts");
        Console.WriteLine("  /prompt <name>        - Invoke an MCP prompt");
        Console.WriteLine("  /providers            - List all available providers");
        Console.WriteLine("  /provider <name>      - Switch to a different provider");
        Console.WriteLine("  ?                     - Show this help");
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