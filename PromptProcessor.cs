using ModelContextProtocol.Protocol;
using Spectre.Console;

namespace nb;

public class PromptProcessor
{
    private readonly McpManager _mcpManager;

    public PromptProcessor(McpManager mcpManager)
    {
        _mcpManager = mcpManager;
    }

    public void DisplayAvailablePrompts()
    {
        var prompts = _mcpManager.GetPrompts();
        if (!prompts.Any())
        {
            AnsiConsole.MarkupLine("[yellow]No prompts available from connected MCP servers[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[green]Available prompts:[/]");
            foreach (var prompt in prompts)
            {
                var description = !string.IsNullOrEmpty(prompt.Description) ? $" - {prompt.Description}" : "";
                AnsiConsole.MarkupLine($"  [white]{prompt.Name}[/]{description}");
            }
        }
    }

    public async Task<string?> InvokePromptAsync(string promptName)
    {
        try
        {
            var prompt = _mcpManager.GetPrompts().FirstOrDefault(p => p.Name == promptName);
            if (prompt == null)
            {
                AnsiConsole.MarkupLine($"[red]Prompt '{promptName}' not found[/]");
                return null;
            }

            var parameters = ExtractParameterNames(prompt.Description);
            var args = new Dictionary<string, object?>();

            // Collect arguments interactively
            foreach (var param in parameters)
            {
                var value = AnsiConsole.Ask<string>($"Enter value for [green]{param}[/]:");
                args[param] = value;
            }

            var result = await prompt.GetAsync(args);

            // Extract text content from the result
            var textContent = string.Empty;
            if (result?.Messages != null)
            {
                foreach (var message in result.Messages)
                {
                    if (message.Content is TextContentBlock textBlock)
                    {
                        textContent += textBlock.Text + "\n";
                    }
                }
            }

            if (!string.IsNullOrEmpty(textContent))
            {
                AnsiConsole.MarkupLine("[green]Prompt result will be sent to the AI[/]");
                return textContent.Trim();
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Prompt returned no text content[/]");
                return null;
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error invoking prompt: {ex.Message}[/]");
            return null;
        }
    }

    private List<string> ExtractParameterNames(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return new List<string>();

        // Look for "Parameters: param1, param2, param3" pattern
        var match = System.Text.RegularExpressions.Regex.Match(description, @"Parameters:\s*(.+)");
        if (!match.Success)
            return new List<string>();

        return match.Groups[1].Value
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList();
    }
}