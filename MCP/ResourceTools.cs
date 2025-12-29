using System.Text.Json;
using Microsoft.Extensions.AI;

namespace nb.MCP;

/// <summary>
/// Native tools that expose MCP resources to the LLM.
/// These tools bridge the gap between MCP servers and the model,
/// allowing the model to discover and access resources without
/// requiring server-side tool implementations.
/// </summary>
public static class ResourceTools
{
    public static AIFunction CreateListResourcesTool(McpManager mcpManager)
    {
        var listResourcesFunc = (string? serverName) =>
        {
            var resources = mcpManager.GetResources();

            // Filter by server if requested
            if (!string.IsNullOrEmpty(serverName))
            {
                resources = resources.Where(r => r.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Format as JSON for the model
            var resourceList = resources.Select(r => new
            {
                uri = r.Uri,
                name = r.Name,
                description = r.Description ?? "",
                mimeType = r.MimeType ?? "text/plain",
                server = r.ServerName
            }).ToList();

            if (resourceList.Count == 0)
            {
                return serverName != null
                    ? $"No resources found for server '{serverName}'"
                    : "No resources available from any connected MCP servers";
            }

            return JsonSerializer.Serialize(resourceList, new JsonSerializerOptions { WriteIndented = true });
        };

        return AIFunctionFactory.Create(
            listResourcesFunc,
            name: "nb_list_resources",
            description: "Lists available MCP resources. Optionally filter by server name. Returns JSON array of resources with uri, name, description, mimeType, and server fields."
        );
    }

    public static AIFunction CreateReadResourceTool(McpManager mcpManager)
    {
        var readResourceFunc = async (string uri) =>
        {
            try
            {
                var content = await mcpManager.ReadResourceAsync(uri);
                return content;
            }
            catch (InvalidOperationException ex)
            {
                return $"Error reading resource: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"Unexpected error reading resource '{uri}': {ex.Message}";
            }
        };

        return AIFunctionFactory.Create(
            readResourceFunc,
            name: "nb_read_resource",
            description: "Reads the content of an MCP resource by URI. Returns the resource content as text, or base64 for binary content. Use nb_list_resources first to discover available resource URIs."
        );
    }
}
