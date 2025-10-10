using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Spectre.Console;
using nb.Utilities;

namespace nb.MCP;

public class McpManager : IDisposable
{
    private readonly List<McpClient> _mcpClients = new();
    private readonly List<AIFunction> _mcpTools = new();
    private readonly List<McpClientPrompt> _mcpPrompts = new();
    private readonly List<string> _connectedServerNames = new();
    private readonly HashSet<string> _alwaysAllowTools = new();

    public async Task InitializeAsync(bool showBanners = true)
    {
        try
        {
            var mcpConfig = LoadMcpConfiguration();
            
            // Use either McpServers or Servers property (support both formats)
            var servers = mcpConfig.McpServers.Count > 0 ? mcpConfig.McpServers : mcpConfig.Servers;
            
            if (servers.Count == 0)
            {
                return; // No MCP servers configured
            }

            foreach (var (serverName, serverConfig) in servers)
            {
                try
                {
                    var envVars = new Dictionary<string, string?>();
                    if (serverConfig.Env != null)
                    {
                        foreach (var kvp in serverConfig.Env)
                        {
                            envVars[kvp.Key] = kvp.Value;
                        }
                    }

                    var transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = serverName,
                        Command = serverConfig.Command,
                        Arguments = serverConfig.Args ?? Array.Empty<string>(),
                        EnvironmentVariables = envVars
                    });

                    var client = await McpClient.CreateAsync(transport);
                    _mcpClients.Add(client);
                    _connectedServerNames.Add(serverName);

                    // Get tools from this client and namespace them
                    var tools = await client.ListToolsAsync();
                    foreach (var tool in tools)
                    {
                        // Namespace the tool: serverName:toolName
                        var namespacedTool = tool.WithName($"{serverName}:{tool.Name}");
                        _mcpTools.Add(namespacedTool);
                    }

                    // Add tools to always-allow list if configured (namespace them)
                    if (serverConfig.AlwaysAllow != null)
                    {
                        foreach (var toolName in serverConfig.AlwaysAllow)
                        {
                            _alwaysAllowTools.Add($"{serverName}:{toolName}");
                        }
                    }

                    // Get prompts from this client
                    var promptCount = 0;
                    try
                    {
                        var prompts = await client.ListPromptsAsync();
                        _mcpPrompts.AddRange(prompts);
                        promptCount = prompts.Count;
                    }
                    catch (Exception)
                    {
                        // Most MCP servers don't support prompts, so we silently ignore this
                    }

                    // Silently connect - no banners
                }
                catch (Exception)
                {
                    // Silently skip failed MCP servers
                }
            }
        }
        catch (Exception)
        {
            // Silently skip MCP config errors
        }
    }

    public IReadOnlyList<AIFunction> GetTools()
    {
        return _mcpTools.AsReadOnly();
    }

    public IReadOnlyList<McpClientPrompt> GetPrompts()
    {
        return _mcpPrompts.AsReadOnly();
    }

    public IReadOnlyList<string> GetConnectedServerNames()
    {
        return _connectedServerNames.AsReadOnly();
    }

    public bool IsAlwaysAllowed(string toolName)
    {
        return _alwaysAllowTools.Contains(toolName);
    }

    public void Dispose()
    {
        foreach (var client in _mcpClients)
        {
            try
            {
                if (client is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    private static McpConfig LoadMcpConfiguration()
    {
        var executablePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        var executableDirectory = Path.GetDirectoryName(executablePath) ?? Directory.GetCurrentDirectory();
        var mcpConfigPath = Path.Combine(executableDirectory, "mcp.json");

        if (!File.Exists(mcpConfigPath))
        {
            return new McpConfig();
        }

        var json = File.ReadAllText(mcpConfigPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        return JsonSerializer.Deserialize<McpConfig>(json, options) ?? new McpConfig();
    }
}

public class McpServerConfig
{
    public string Command { get; set; } = string.Empty;
    public string[]? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public int Timeout { get; set; } = 60000;
    public string[]? AlwaysAllow { get; set; }
}

public class McpConfig
{
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();
}