using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Spectre.Console;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using nb.Utilities;

namespace nb.MCP;

public class FakeToolManager
{
    private static readonly Regex MacroRegex = new(@"\{\{\$(\w+(?:\.\w+)*)(?:\(([^)]*)\))?\}\}", RegexOptions.Compiled);

    private readonly List<FakeTool> _fakeTools = new();
    private readonly List<string> _overriddenTools = new();
    private readonly Dictionary<string, int> _counters = new();

    public async Task<FakeToolLoadResult> LoadFakeToolsAsync(string filePath = "fake-tools.yaml")
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new FakeToolLoadResult { Success = true, ToolsLoaded = 0, ToolsOverridden = 0 };
            }

            var yamlContent = await File.ReadAllTextAsync(filePath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .Build();

            var config = deserializer.Deserialize<FakeToolConfig>(yamlContent);

            if (config?.FakeTools == null)
            {
                return new FakeToolLoadResult { Success = true, ToolsLoaded = 0, ToolsOverridden = 0 };
            }

            _fakeTools.Clear();
            _fakeTools.AddRange(config.FakeTools);

            return new FakeToolLoadResult
            {
                Success = true,
                ToolsLoaded = _fakeTools.Count,
                ToolsOverridden = 0 // Will be calculated during integration
            };
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: Failed to load fake tools: {Markup.Escape(ex.Message)}[/]");
            return new FakeToolLoadResult { Success = false, ToolsLoaded = 0, ToolsOverridden = 0 };
        }
    }

    public List<AIFunction> IntegrateWithMcpTools(IReadOnlyList<AIFunction> mcpTools)
    {
        _overriddenTools.Clear();
        var allTools = new List<AIFunction>(mcpTools);

        foreach (var fakeTool in _fakeTools)
        {
            // Check if this fake tool overrides an existing MCP tool
            var existingToolIndex = allTools.FindIndex(t => t.Name == fakeTool.Name);
            if (existingToolIndex >= 0)
            {
                // Override existing tool
                allTools[existingToolIndex] = CreateAIFunctionFromFakeTool(fakeTool);
                _overriddenTools.Add(fakeTool.Name);
            }
            else
            {
                // Add new tool
                allTools.Add(CreateAIFunctionFromFakeTool(fakeTool));
            }
        }

        return allTools;
    }

    public IReadOnlyList<string> GetOverriddenTools()
    {
        return _overriddenTools.AsReadOnly();
    }

    public FakeTool? GetFakeTool(string name)
    {
        return _fakeTools.FirstOrDefault(t => t.Name == name);
    }

    public string ExpandMacros(string template, IDictionary<string, object?>? arguments)
    {
        return MacroRegex.Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            var args = match.Groups[2].Success ? match.Groups[2].Value : null;

            // Handle dotted names - first segment is the macro type
            var segments = name.Split('.', 2);
            var macroType = segments[0];

            return macroType switch
            {
                "guid" => Guid.NewGuid().ToString(),
                "timestamp" => DateTime.UtcNow.ToString("o"),
                "int" => ExpandInt(args),
                "counter" => ExpandCounter(name),
                "param" => ExpandParam(segments.Length > 1 ? segments[1] : args, arguments),
                "choice" => ExpandChoice(args),
                "random_string" => ExpandRandomString(args),
                _ => match.Value // Leave unrecognized macros as literal text
            };
        });
    }

    private static string ExpandInt(string? args)
    {
        if (args != null)
        {
            var parts = args.Split(',', 2);
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out var min) && int.TryParse(parts[1].Trim(), out var max))
                return Random.Shared.Next(min, max).ToString();
        }
        return Random.Shared.Next().ToString();
    }

    private string ExpandCounter(string name)
    {
        _counters.TryGetValue(name, out var current);
        _counters[name] = ++current;
        return current.ToString();
    }

    private static string ExpandParam(string? paramName, IDictionary<string, object?>? arguments)
    {
        if (paramName == null || arguments == null)
            return "";
        return arguments.TryGetValue(paramName, out var value) ? value?.ToString() ?? "" : "";
    }

    private static string ExpandChoice(string? args)
    {
        if (args == null) return "";
        var choices = args.Split(',');
        return choices[Random.Shared.Next(choices.Length)].Trim();
    }

    private static string ExpandRandomString(string? args)
    {
        var length = 8;
        if (args != null && int.TryParse(args.Trim(), out var parsed))
            length = parsed;

        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, length).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    private static AIFunction CreateAIFunctionFromFakeTool(FakeTool fakeTool)
    {
        // Build description with parameter documentation
        var description = new StringBuilder(fakeTool.Description);

        if (fakeTool.Parameters.Count > 0)
        {
            description.AppendLine();
            description.AppendLine();
            description.AppendLine("Parameters:");
            foreach (var param in fakeTool.Parameters)
            {
                var requiredStr = param.Required ? " (required)" : "";
                description.AppendLine($"- {param.Name}: {param.Type}{requiredStr} - {param.Description}");
            }
        }

        // Accept a generic dictionary to capture all arguments
        // The actual invocation is handled by ConversationManager
        return AIFunctionFactory.Create(
            (IDictionary<string, object?> parameters) => fakeTool.Response,
            name: fakeTool.Name,
            description: description.ToString()
        );
    }
}

public class FakeToolConfig
{
    public List<FakeTool> FakeTools { get; set; } = new();
}

public class FakeTool
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<FakeToolParameter> Parameters { get; set; } = new();
    public string Response { get; set; } = string.Empty;
}

public class FakeToolParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;
    public bool Required { get; set; } = false;
}

public class FakeToolLoadResult
{
    public bool Success { get; set; }
    public int ToolsLoaded { get; set; }
    public int ToolsOverridden { get; set; }
}