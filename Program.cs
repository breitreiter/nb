using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using System.Runtime.InteropServices;

namespace nb;

public class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);
    
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);
    
    private static string _systemPrompt = string.Empty;
    private static ChatClient _client;
    private static List<ChatMessage> _conversationHistory = new List<ChatMessage>();
    private static bool _stopSpinner = false;

    public static async Task Main(string[] args)
    {
        // Set UTF-8 code page for proper Unicode support on Windows
        try
        {
            SetConsoleCP(65001);
            SetConsoleOutputCP(65001);
        }
        catch
        {
            // Ignore errors on non-Windows platforms
        }
        
        var config = LoadConfiguration();
        InitializeOpenAIClient(config);

        var initialPrompt = string.Join(" ", args);

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (!string.IsNullOrEmpty(initialPrompt))
        {
            await SendMessage(initialPrompt);
        }

        await StartChatLoop();
    }

    private static IConfiguration LoadConfiguration()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        return config;
    }

    private static void InitializeOpenAIClient(IConfiguration config)
    {
        var endpoint = config["AzureOpenAI:Endpoint"];
        var apiKey = config["AzureOpenAI:ApiKey"];
        var deployment = config["AzureOpenAI:DeploymentName"] ?? "o4-mini";

        _systemPrompt = LoadSystemPrompt();

        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(apiKey))
        {
            AnsiConsole.MarkupLine("[red]Error: Azure OpenAI endpoint and API key must be configured in appsettings.json[/]");
            Environment.Exit(1);
        }

        var endpointUri = new Uri(endpoint);

        AzureOpenAIClientOptions options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        AzureOpenAIClient azureClient = new(
            endpointUri,
            new AzureKeyCredential(apiKey), 
            options);
        _client = azureClient.GetChatClient(deployment);
        
        // Initialize conversation history with system message
        _conversationHistory.Add(new SystemChatMessage(_systemPrompt));
    }

    private static string LoadSystemPrompt()
    {
        try
        {
            var executablePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var executableDirectory = Path.GetDirectoryName(executablePath);
            var systemPromptPath = Path.Combine(executableDirectory, "system.md");
            
            if (File.Exists(systemPromptPath))
            {
                return File.ReadAllText(systemPromptPath);
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Warning: system.md file not found. Using default system prompt.[/]");
                return "You are a helpful AI assistant.";
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error loading system prompt: {ex.Message}[/]");
            return "You are a helpful AI assistant.";
        }
    }

    private static async Task SendMessage(string userMessage)
    {
        if (_client == null) return;

        try
        {
            // Support for this recently-launched model with MaxOutputTokenCount parameter requires
            // Azure.AI.OpenAI 2.2.0-beta.4 and SetNewMaxCompletionTokensPropertyEnabled
            var requestOptions = new ChatCompletionOptions()
            {
                MaxOutputTokenCount = 10000,
            };

            // The SetNewMaxCompletionTokensPropertyEnabled() method is an [Experimental] opt-in to use
            // the new max_completion_tokens JSON property instead of the legacy max_tokens property.
            // This extension method will be removed and unnecessary in a future service API version;
            // please disable the [Experimental] warning to acknowledge.
#pragma warning disable AOAI001
            requestOptions.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

            // Add user message to conversation history
            _conversationHistory.Add(new UserChatMessage(userMessage));

            // Start spinner and make async API call
            var spinnerTask = Task.Run(() => ShowSpinner());
            var apiTask = _client.CompleteChatAsync(_conversationHistory, requestOptions);
            
            var response = await apiTask;
            
            // Stop spinner
            _stopSpinner = true;
            await spinnerTask;
            
            var assistantMessage = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;

            if (!string.IsNullOrEmpty(assistantMessage))
            {
                // Add assistant response to conversation history
                _conversationHistory.Add(new AssistantChatMessage(assistantMessage));
                RenderMarkdown(assistantMessage);
            }
        }
        catch (Exception ex)
        {
            // Stop spinner on error
            _stopSpinner = true;
            AnsiConsole.MarkupLine($"[red]Error: {ex.Message}[/]");
        }
        finally
        {
            // Reset spinner flag for next call
            _stopSpinner = false;
        }
    }

    private static void ShowSpinner()
    {
        var spinnerChars = new char[] { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };
        int index = 0;
        
        Console.CursorVisible = false;
        
        while (!_stopSpinner)
        {
            Console.Write($"\r{spinnerChars[index]} Thinking...");
            index = (index + 1) % spinnerChars.Length;
            Thread.Sleep(100);
        }
        
        Console.Write("\r                    \r"); // Clear the spinner line
        Console.CursorVisible = true;
    }

    private static void RenderMarkdown(string markdown)
    {
        Rule rule = new("[blue]nb[/]")
        {
            Justification = Justify.Left,
            Border = BoxBorder.Rounded,
            Style = "blue"
        };
        AnsiConsole.Write(rule);

        AnsiConsole.WriteLine(markdown);

        rule.Title = null;
        AnsiConsole.Write(rule);
    }

    private static async Task StartChatLoop()
    {
        AnsiConsole.MarkupLine("[white]N[/]ota[white]B[/]ene 0.1α [grey]Type 'exit' to quit.[/]");

        while (true)
        {
            var userInput = AnsiConsole.Ask<string>("[yellow]You:[/]");

            if (userInput.ToLower() == "exit")
                break;

            if (!string.IsNullOrWhiteSpace(userInput))
            {
                await SendMessage(userInput);
            }
        }
    }
}