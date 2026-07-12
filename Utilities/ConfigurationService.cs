using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace nb.Utilities;

public class ConfigurationService
{
    private readonly IConfiguration _configuration;
    private readonly string _systemPrompt;

    // configPath (from --config) selects a hermetic single-file config; null
    // uses the layered install/user/project resolution.
    public ConfigurationService(string? configPath = null)
    {
        _configuration = LoadConfiguration(configPath);
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

    private static IConfiguration LoadConfiguration(string? configPath)
    {
        var config = BuildConfiguration(configPath, Directory.GetCurrentDirectory());
        ExpandEnvironmentReferences(config);
        return config;
    }

    // Git-style layered resolution (later wins). With an explicit --config path
    // the file layers collapse to just that file (hermetic runs); otherwise:
    // install defaults, then user (~/.config/nb), then the nearest project
    // .nb/config.json walking up from cwd. The NB_ environment layer is applied
    // last in every case (NB_ActiveProvider -> ActiveProvider,
    // NB_ChatProviders__0__ApiKey -> nested), so CI can inject config and keys
    // without a file. Split out and internal so config resolution is testable.
    internal static IConfigurationRoot BuildConfiguration(string? configPath, string cwd)
    {
        var builder = new ConfigurationBuilder();

        if (configPath != null)
        {
            // Named deliberately — fail fast if it's missing (Program surfaces this).
            builder.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
        }
        else
        {
            builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: true);
            builder.AddJsonFile(UserConfigPath(), optional: true, reloadOnChange: true);

            var projectConfig = FindProjectConfig(cwd);
            if (projectConfig != null)
                builder.AddJsonFile(projectConfig, optional: true, reloadOnChange: true);
        }

        builder.AddEnvironmentVariables("NB_");
        return builder.Build();
    }

    // ~/.config/nb/config.json, honoring XDG_CONFIG_HOME. Returned even if absent
    // (added as an optional layer); a fresh install simply has no user config yet.
    private static string UserConfigPath()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(baseDir, "nb", "config.json");
    }

    // Nearest .nb/config.json walking up from cwd — the same upward walk NB.md uses.
    private static string? FindProjectConfig(string cwd)
    {
        var dir = Path.GetFullPath(cwd);
        while (dir != null)
        {
            var candidate = Path.Combine(dir, ".nb", "config.json");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // Resolve ${VAR} references in config values against environment variables, so
    // secrets (e.g. an API key) can live in the environment, never in the JSON.
    private static void ExpandEnvironmentReferences(IConfigurationRoot config)
    {
        var envRef = new Regex(@"\$\{(\w+)\}");
        foreach (var (key, value) in config.AsEnumerable())
        {
            if (string.IsNullOrEmpty(value) || !value.Contains("${")) continue;

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