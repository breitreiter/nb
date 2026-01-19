using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace nb.MCP;

public class FakeToolManager
{
    private readonly List<FakeTool> _fakeTools = new();
    private readonly List<string> _overriddenTools = new();

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
            Console.WriteLine($"Warning: Failed to load fake tools: {ex.Message}");
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