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
    // NB_ChatProviders__0__ApiKey -> nested), plus friendly aliases
    // (NB_PROVIDER, NB_MODEL) via ApplyFriendlyEnvAliases, so CI can inject config
    // and keys without a file. Split out and internal so config resolution is testable.
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
        var config = builder.Build();
        ApplyFriendlyEnvAliases(config);
        return config;
    }

    // Friendly aliases for the common config knobs, so CI doesn't need the raw
    // nested NB_ paths. NB_PROVIDER -> ActiveProvider; NB_MODEL -> the active
    // provider's model field. They ride the env layer, so they apply even to a
    // hermetic --config run. NB_OUTPUT / NB_SPEC are program-state, handled in
    // Program (they set flag defaults, not config keys).
    private static void ApplyFriendlyEnvAliases(IConfigurationRoot config)
    {
        var provider = Environment.GetEnvironmentVariable("NB_PROVIDER");
        if (!string.IsNullOrEmpty(provider))
            config["ActiveProvider"] = provider;

        var model = Environment.GetEnvironmentVariable("NB_MODEL");
        if (string.IsNullOrEmpty(model)) return;

        var active = config["ActiveProvider"];
        var children = config.GetSection("ChatProviders").GetChildren().ToList();
        for (int i = 0; i < children.Count; i++)
            if (string.Equals(children[i]["Name"], active, StringComparison.OrdinalIgnoreCase))
            {
                // Write both keys: most providers read "Model", but classic
                // AzureOpenAI reads "ChatDeploymentName" (mirrors Program.OverrideProviderModel).
                config[$"ChatProviders:{i}:Model"] = model;
                config[$"ChatProviders:{i}:ChatDeploymentName"] = model;
                return;
            }
    }

    // ~/.config/nb, honoring XDG_CONFIG_HOME — the user config layer's directory.
    internal static string UserNbDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(baseDir, "nb");
    }

    // Nearest .nb/<fileName> walking up from cwd — the same upward walk NB.md uses.
    // Shared by the config layer (config.json) and the MCP layer (mcp.json).
    internal static string? FindProjectNbFile(string cwd, string fileName)
    {
        var dir = Path.GetFullPath(cwd);
        while (dir != null)
        {
            var candidate = Path.Combine(dir, ".nb", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    // ~/.config/nb/config.json — returned even if absent (added as an optional layer).
    private static string UserConfigPath() => Path.Combine(UserNbDir(), "config.json");

    private static string? FindProjectConfig(string cwd) => FindProjectNbFile(cwd, "config.json");

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