using System.Text.Json;
using Spectre.Console;
using nb.Utilities;

namespace nb;

public record Kit(string Name, string Description, string? Prompt, string? PromptFile, string[] McpServers);

public class KitManager
{
    private readonly Dictionary<string, Kit> _kits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _activeKits = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> ActiveKitNames => _activeKits;
    public IReadOnlyDictionary<string, Kit> Kits => _kits;

    public void LoadKits(string baseDirectory)
    {
        var kitsFile = Path.Combine(baseDirectory, "kits.json");
        if (!File.Exists(kitsFile)) return;

        try
        {
            var json = File.ReadAllText(kitsFile);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };
            var config = JsonSerializer.Deserialize<KitsConfig>(json, options);
            if (config?.Kits == null) return;

            foreach (var (name, def) in config.Kits)
            {
                var prompt = def.Prompt;
                if (string.IsNullOrEmpty(prompt) && !string.IsNullOrEmpty(def.PromptFile))
                {
                    var promptPath = Path.IsPathRooted(def.PromptFile)
                        ? def.PromptFile
                        : Path.Combine(baseDirectory, def.PromptFile);
                    if (File.Exists(promptPath))
                        prompt = File.ReadAllText(promptPath).Trim();
                }

                _kits["+" + name] = new Kit(
                    "+" + name,
                    def.Description ?? name,
                    prompt,
                    def.PromptFile,
                    def.McpServers ?? Array.Empty<string>()
                );
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to load kits.json: {Markup.Escape(ex.Message)}[/]");
        }
    }

    public bool Activate(string kitName)
    {
        if (!_kits.ContainsKey(kitName)) return false;
        _activeKits.Add(kitName);
        return true;
    }

    public bool Deactivate(string kitName)
    {
        return _activeKits.Remove(kitName);
    }

    public void ClearActive()
    {
        _activeKits.Clear();
    }

    public string? GetCombinedPrompt()
    {
        var prompts = _activeKits
            .Where(name => _kits.ContainsKey(name) && !string.IsNullOrEmpty(_kits[name].Prompt))
            .Select(name => _kits[name].Prompt!)
            .ToList();

        return prompts.Count > 0 ? string.Join("\n\n---\n\n", prompts) : null;
    }

    public HashSet<string> GetActiveMcpServers()
    {
        var servers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _activeKits)
        {
            if (_kits.TryGetValue(name, out var kit))
            {
                foreach (var server in kit.McpServers)
                    servers.Add(server);
            }
        }
        return servers;
    }
}

internal class KitsConfig
{
    public Dictionary<string, KitDefinition>? Kits { get; set; }
}

internal class KitDefinition
{
    public string? Description { get; set; }
    public string? Prompt { get; set; }
    public string? PromptFile { get; set; }
    public string[]? McpServers { get; set; }
}
