using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace nb.Harness;

/// <summary>
/// A tool declaration: name, description and JSON schema, with no invocation behind it.
///
/// Native tools are hand-dispatched by <see cref="ConversationManager"/> on the call's
/// name — the <see cref="AIFunction"/> is only ever a declaration, never invoked — so a
/// costume can declare whatever shape its target harness advertises without touching the
/// implementation behind it. That is the whole mechanism harness emulation runs on.
/// </summary>
internal sealed class DeclaredFunction : AIFunction
{
    public DeclaredFunction(string name, string description, JsonElement schema)
    {
        Name = name;
        Description = description;
        JsonSchema = schema;
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    protected override ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<object?>(null);
}

/// <summary>Fluent construction of a tool's JSON schema, so costume tables read as data.</summary>
internal sealed class SchemaBuilder
{
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = new();

    public SchemaBuilder Add(string name, string type, string description, bool required = false)
    {
        _properties[name] = new JsonObject { ["type"] = type, ["description"] = description };
        if (required) _required.Add(name);
        return this;
    }

    /// <summary>A string parameter constrained to a fixed set of values.</summary>
    public SchemaBuilder AddEnum(string name, string[] values, string description, bool required = false)
    {
        var allowed = new JsonArray();
        foreach (var v in values) allowed.Add(v);
        _properties[name] = new JsonObject { ["type"] = "string", ["enum"] = allowed, ["description"] = description };
        if (required) _required.Add(name);
        return this;
    }

    /// <summary>An array of objects, shaped by a nested builder.</summary>
    public SchemaBuilder AddArray(string name, string description, SchemaBuilder item, bool required = false)
    {
        _properties[name] = new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = item.BuildNode(),
        };
        if (required) _required.Add(name);
        return this;
    }

    public JsonElement Build() => JsonSerializer.SerializeToElement(BuildNode());

    private JsonObject BuildNode() => new()
    {
        ["type"] = "object",
        ["properties"] = _properties,
        ["required"] = _required,
    };
}
