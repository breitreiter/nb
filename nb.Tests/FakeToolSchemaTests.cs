using System.Text.Json;
using Microsoft.Extensions.AI;
using nb.MCP;

namespace nb.Tests;

/// <summary>
/// Fake tools declare parameters in fake-tools.yaml; this asserts those declarations
/// reach the wire as a real JSON schema.
///
/// They used to not: the function was registered as
/// <c>(IDictionary&lt;string, object?&gt; parameters)</c>, so the emitted schema was a
/// single opaque <c>parameters</c> object and the declared names, types and
/// required-ness existed only in the description prose. Harness emulation
/// (plans/harness-emulation.md) stands fake tools in for tools nb does not implement,
/// where the advertised schema is the entire point.
/// </summary>
public class FakeToolSchemaTests : IDisposable
{
    private readonly string _dir;

    public FakeToolSchemaTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"nb-test-faketool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task DeclaredParameters_BecomeSchemaProperties()
    {
        var schema = await SchemaFor("""
            fake_tools:
              - name: search_docs
                description: Search the documentation
                parameters:
                  - name: query
                    type: string
                    description: What to search for
                    required: true
                  - name: limit
                    type: integer
                    description: Max results
                    required: false
                response: nothing found
            """, "search_docs");

        Assert.Equal("object", schema.GetProperty("type").GetString());

        var props = schema.GetProperty("properties");
        Assert.Equal("string", props.GetProperty("query").GetProperty("type").GetString());
        Assert.Equal("What to search for", props.GetProperty("query").GetProperty("description").GetString());
        Assert.Equal("integer", props.GetProperty("limit").GetProperty("type").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "query" }, required);
    }

    /// <summary>The regression guard: the old opaque wrapper must not come back.</summary>
    [Fact]
    public async Task Schema_HasNoOpaqueParametersWrapper()
    {
        var schema = await SchemaFor("""
            fake_tools:
              - name: ping
                description: Ping something
                parameters:
                  - name: host
                    type: string
                    required: true
                response: pong
            """, "ping");

        var names = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "host" }, names);
    }

    [Theory]
    [InlineData("int", "integer")]
    [InlineData("long", "integer")]
    [InlineData("bool", "boolean")]
    [InlineData("float", "number")]
    [InlineData("double", "number")]
    [InlineData("str", "string")]
    [InlineData("list", "array")]
    [InlineData("dict", "object")]
    [InlineData("BOOLEAN", "boolean")]
    public async Task AuthorFriendlyTypeNames_MapOntoJsonSchemaTypes(string declared, string expected)
    {
        var schema = await SchemaFor($"""
            fake_tools:
              - name: probe
                description: Probe
                parameters:
                  - name: value
                    type: {declared}
                    required: true
                response: ok
            """, "probe");

        Assert.Equal(expected, schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    /// <summary>An unrecognised type passes through rather than being coerced to string —
    /// a wrong type is easier to notice than a quietly rewritten one.</summary>
    [Fact]
    public async Task UnrecognisedType_PassesThrough()
    {
        var schema = await SchemaFor("""
            fake_tools:
              - name: odd
                description: Odd
                parameters:
                  - name: value
                    type: timestamp
                    required: true
                response: ok
            """, "odd");

        Assert.Equal("timestamp", schema.GetProperty("properties").GetProperty("value").GetProperty("type").GetString());
    }

    [Fact]
    public async Task NoParameters_YieldsEmptyPropertiesAndRequired()
    {
        var schema = await SchemaFor("""
            fake_tools:
              - name: heartbeat
                description: Heartbeat
                response: alive
            """, "heartbeat");

        Assert.Empty(schema.GetProperty("properties").EnumerateObject());
        Assert.Empty(schema.GetProperty("required").EnumerateArray());
    }

    /// <summary>Parameters stay documented in the description too, matching how nb's
    /// native tools present themselves.</summary>
    [Fact]
    public async Task Description_StillDocumentsParameters()
    {
        var tool = await ToolFor("""
            fake_tools:
              - name: search_docs
                description: Search the documentation
                parameters:
                  - name: query
                    type: string
                    description: What to search for
                    required: true
                response: nothing found
            """, "search_docs");

        Assert.Contains("Search the documentation", tool.Description);
        Assert.Contains("query: string (required) - What to search for", tool.Description);
    }

    // ---- harness ----

    private async Task<JsonElement> SchemaFor(string yaml, string toolName) =>
        (await ToolFor(yaml, toolName)).JsonSchema;

    private async Task<AIFunction> ToolFor(string yaml, string toolName)
    {
        var path = Path.Combine(_dir, $"fake-tools-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, yaml);

        var manager = new FakeToolManager();
        var load = await manager.LoadFakeToolsAsync(path);
        Assert.True(load.Success, "fake-tools.yaml failed to load");

        var tools = manager.IntegrateWithMcpTools(Array.Empty<AIFunction>());
        var tool = tools.FirstOrDefault(t => t.Name == toolName);
        Assert.NotNull(tool);
        return tool!;
    }
}
