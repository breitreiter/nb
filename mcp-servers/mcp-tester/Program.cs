using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;

public class Program
{
    record PromptInfo(Delegate Function, List<string> ParameterNames, string OriginalContent);
    static Dictionary<string, PromptInfo> DynamicPromptMethods = new();

    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            // Configure all logs to go to stderr
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Generate dynamic methods on Prompts class
        GeneratePromptMethods();

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithPrompts(CreatePrompts())
            .WithResources(CreateResources());

        await builder.Build().RunAsync();
    }

    static void GeneratePromptMethods()
    {
        var promptsDir = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "Prompts");
        if (!Directory.Exists(promptsDir)) return;

        var mdFiles = Directory.GetFiles(promptsDir, "*.md");

        foreach (var filePath in mdFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var content = File.ReadAllText(filePath);
            
            // Extract all placeholders using regex
            var placeholderRegex = new Regex(@"\{([^}]+)\}", RegexOptions.Compiled);
            var matches = placeholderRegex.Matches(content);
            
            // Get unique parameter names from placeholders
            var parameterNames = matches.Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            
            // Create a method name from the filename (e.g., "fave_color" -> "favecolor")
            var methodName = fileName.Replace("_", "").Replace("-", "").ToLower();
            
            // Create dynamic method with proper signature
            var methodDelegate = CreateDynamicMethod(parameterNames, content);
            
            // Store the prompt info
            DynamicPromptMethods[methodName] = new PromptInfo(methodDelegate, parameterNames, content);
        }
    }

    static IEnumerable<McpServerPrompt> CreatePrompts()
    {
        // Create prompts from dynamic methods
        foreach (var kvp in DynamicPromptMethods)
        {
            var methodName = kvp.Key;
            var promptInfo = kvp.Value;
            
            // If there are no parameters, just return the content as-is
            if (promptInfo.ParameterNames.Count == 0)
            {
                var simpleDelegate = new Func<string>(() => promptInfo.OriginalContent);
                yield return McpServerPrompt.Create(
                    simpleDelegate,
                    new McpServerPromptCreateOptions 
                    { 
                        Name = methodName,
                        Description = $"Prompt from {methodName}.md file."
                    }
                );
            }
            else
            {
                // Use the dynamically created delegate with proper signature
                yield return McpServerPrompt.Create(
                    promptInfo.Function,
                    new McpServerPromptCreateOptions 
                    { 
                        Name = methodName,
                        Description = $"Prompt from {methodName}.md file. Parameters: {string.Join(", ", promptInfo.ParameterNames)}"
                    }
                );
            }
        }
    }

    // Static template methods for different parameter counts
    static string ProcessTemplate0(string template) => template;
    
    static string ProcessTemplate1(string template, string p0, string param0Name)
    {
        return template.Replace($"{{{param0Name}}}", p0);
    }
    
    static string ProcessTemplate2(string template, string p0, string p1, string param0Name, string param1Name)
    {
        return template.Replace($"{{{param0Name}}}", p0).Replace($"{{{param1Name}}}", p1);
    }
    
    static string ProcessTemplate3(string template, string p0, string p1, string p2, string param0Name, string param1Name, string param2Name)
    {
        return template.Replace($"{{{param0Name}}}", p0).Replace($"{{{param1Name}}}", p1).Replace($"{{{param2Name}}}", p2);
    }

    static Delegate CreateDynamicMethod(List<string> parameterNames, string content)
    {
        return parameterNames.Count switch
        {
            0 => new Func<string>(() => ProcessTemplate0(content)),
            1 => new Func<string, string>(p0 => ProcessTemplate1(content, p0, parameterNames[0])),
            2 => new Func<string, string, string>((p0, p1) => ProcessTemplate2(content, p0, p1, parameterNames[0], parameterNames[1])),
            3 => new Func<string, string, string, string>((p0, p1, p2) => ProcessTemplate3(content, p0, p1, p2, parameterNames[0], parameterNames[1], parameterNames[2])),
            _ => throw new NotSupportedException($"Too many parameters: {parameterNames.Count}. Maximum supported is 3.")
        };
    }

    static IEnumerable<McpServerResource> CreateResources()
    {
        // A silly test resource that won't conflict with real work
        // Simple function that returns the haiku text
        Func<string> getHaikus = () => @"Random Haikus for Testing

MCP resources work
Data flows through the server
Testing is complete

Knock knock who is there
It's your friendly test data
Please ignore this file

Silly haikus dance
Across the context window
Do not use for work";

        yield return McpServerResource.Create(
            getHaikus,
            new McpServerResourceCreateOptions
            {
                UriTemplate = "test://haikus",
                Name = "Test Haikus",
                Description = "A collection of silly haikus for testing MCP resource support. Not for actual use!",
                MimeType = "text/plain"
            }
        );
    }
}

[McpServerToolType]
public static class TestTools
{
    [McpServerTool, Description("Echoes the message back to the client.")]
    public static string Echo(string message) => $"Hello from nb's built-in MCP server: {message}";

    [McpServerTool, Description("Echoes in reverse the message sent by the client.")]
    public static string ReverseEcho(string message) => new string(message.Reverse().ToArray());

    [McpServerTool, Description("Returns the current date and time.")]
    public static string CurrentTime() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// Tools that intentionally misbehave for testing client resilience.
/// </summary>
[McpServerToolType]
public static class ChaosTools
{
    [McpServerTool, Description("Throws an exception. Use to test error handling.")]
    public static string ThrowError(string message) => throw new InvalidOperationException($"Intentional error: {message}");

    [McpServerTool, Description("Hangs forever. Use to test timeout handling. WARNING: Will block until killed.")]
    public static string HangForever()
    {
        Thread.Sleep(Timeout.Infinite);
        return "This will never be reached";
    }

    [McpServerTool, Description("Responds after a delay. Use to test slow response handling.")]
    public static string SlowResponse(int delaySeconds)
    {
        Thread.Sleep(TimeSpan.FromSeconds(Math.Min(delaySeconds, 300))); // Cap at 5 minutes
        return $"Responded after {delaySeconds} second delay";
    }

    [McpServerTool, Description("Returns null. Use to test null response handling.")]
    public static string? ReturnNull() => null;

    [McpServerTool, Description("Returns an empty string. Use to test empty response handling.")]
    public static string ReturnEmpty() => string.Empty;
}