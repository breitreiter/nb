using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public IReadOnlyList<string> GetFakeToolNames()
    {
        return _fakeTools.Select(t => t.Name).ToList().AsReadOnly();
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
        // Build description with parameter documentation. The schema carries the same
        // information, but nb's native tools document parameters in prose too, so a
        // fake tool that stands in for one should look the same to the model.
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

        return new FakeAIFunction(fakeTool.Name, description.ToString(), BuildSchema(fakeTool), fakeTool.Response);
    }

    /// <summary>
    /// Emit a real JSON schema from the declared parameters.
    ///
    /// This used to register the function as <c>(IDictionary&lt;string, object?&gt; parameters)</c>,
    /// which reflected to a single opaque <c>parameters</c> object — the declared names,
    /// types and required-ness never reached the wire at all, and the model had to guess
    /// the shape from the prose. Harness emulation (plans/harness-emulation.md) leans on
    /// fake tools to stand in for tools nb does not implement, and a costume's whole
    /// value is the schema it advertises, so the shape has to be real.
    /// </summary>
    private static JsonElement BuildSchema(FakeTool fakeTool)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var param in fakeTool.Parameters)
        {
            var property = new JsonObject { ["type"] = NormalizeType(param.Type) };
            if (!string.IsNullOrWhiteSpace(param.Description))
                property["description"] = param.Description;

            properties[param.Name] = property;
            if (param.Required) required.Add(param.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        };

        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>
    /// fake-tools.yaml is hand-authored, so accept the C#/YAML spellings an author will
    /// reach for and map them onto JSON Schema's type names. An unrecognised type passes
    /// through untouched rather than being silently coerced — a wrong type in the emitted
    /// schema is easier to spot than one quietly rewritten to "string".
    /// </summary>
    private static string NormalizeType(string? type) => (type ?? "string").Trim().ToLowerInvariant() switch
    {
        "int" or "int32" or "int64" or "long" or "integer" => "integer",
        "float" or "double" or "decimal" or "number" => "number",
        "bool" or "boolean" => "boolean",
        "str" or "string" => "string",
        "list" or "array" => "array",
        "dict" or "object" => "object",
        var other => other,
    };

    /// <summary>
    /// A fake tool's advertised surface. <see cref="ConversationManager"/> intercepts
    /// fake-tool calls by name and expands the response macros itself, so invocation here
    /// is the un-taken path — it returns the raw (unexpanded) response for any caller
    /// that does drive the function directly.
    /// </summary>
    private sealed class FakeAIFunction : AIFunction
    {
        private readonly string _response;

        public FakeAIFunction(string name, string description, JsonElement schema, string response)
        {
            Name = name;
            Description = description;
            JsonSchema = schema;
            _response = response;
        }

        public override string Name { get; }
        public override string Description { get; }
        public override JsonElement JsonSchema { get; }

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(_response);
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