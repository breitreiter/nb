using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace nb.Utilities;

public class ConfigurationService
{
    private readonly IConfiguration _configuration;
    private readonly string _systemPrompt;

    public ConfigurationService()
    {
        _configuration = LoadConfiguration();
        _systemPrompt = LoadSystemPrompt();
        SetupConsoleEncoding();
    }

    public IConfiguration GetConfiguration() => _configuration;
    public string GetSystemPrompt() => _systemPrompt;

    public void SetupConsoleEncoding()
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
        
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    private static IConfiguration LoadConfiguration()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        ExpandEnvironmentReferences(config);
        return config;
    }

    // Resolve ${VAR} references in config values against environment variables, so
    // secrets (e.g. an API key) can live in the environment, never in the JSON.
    private static void ExpandEnvironmentReferences(IConfigurationRoot config)
    {
        var envRef = new Regex(@"\$\{(\w+)\}");
        foreach (var (key, value) in config.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value) || !value.Contains("${")) continue;
            if (IsCommentKey(key)) continue;

            config[key] = envRef.Replace(value, m =>
            {
                var name = m.Groups[1].Value;
                var resolved = Environment.GetEnvironmentVariable(name);
                if (resolved is null)
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: environment variable '{name}' referenced in appsettings.json is not set[/]");
                return resolved ?? "";
            });
        }
    }

    // "//"-prefixed keys are comments by convention in appsettings.json. They are
    // never read as settings, and the comments documenting the ${VAR} syntax
    // contain a literal ${VAR} — so expanding them warns about a variable nobody
    // meant to reference. Keys are colon-delimited paths, so test the last segment.
    private static bool IsCommentKey(string key) =>
        key.AsSpan(key.LastIndexOf(':') + 1).StartsWith("//");

    private static string LoadSystemPrompt()
    {
        try
        {
            var systemPromptPath = Path.Combine(AppContext.BaseDirectory, "prompts", "system.md");
            
            if (File.Exists(systemPromptPath))
            {
                return File.ReadAllText(systemPromptPath);
            }
            else
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: system.md file not found. Using default system prompt.[/]");
                return "You are a helpful AI assistant.";
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error loading system prompt: {Markup.Escape(ex.Message)}[/]");
            return "You are a helpful AI assistant.";
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleCP(uint wCodePageID);
    
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint wCodePageID);
}