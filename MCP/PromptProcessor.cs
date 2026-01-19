using ModelContextProtocol.Protocol;

namespace nb.MCP;

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
            Console.WriteLine("No prompts available from connected MCP servers");
        }
        else
        {
            Console.WriteLine("Available prompts:");
            foreach (var prompt in prompts)
            {
                var description = !string.IsNullOrEmpty(prompt.Description) ? $" - {prompt.Description}" : "";
                Console.WriteLine($"  {prompt.Name}{description}");
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
                Console.WriteLine($"Prompt '{promptName}' not found");
                return null;
            }

            var parameters = ExtractParameterNames(prompt.Description);
            var args = new Dictionary<string, object?>();

            // Collect arguments interactively
            foreach (var param in parameters)
            {
                Console.Write($"Enter value for {param}: ");
                var value = Console.ReadLine() ?? "";
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
                Console.WriteLine("Prompt result will be sent to the AI");
                return textContent.Trim();
            }
            else
            {
                Console.WriteLine("Prompt returned no text content");
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error invoking prompt: {ex.Message}");
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