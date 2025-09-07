using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using Spectre.Console;

namespace nb;

public class McpManager : IDisposable
{
    private readonly List<IMcpClient> _mcpClients = new();
    private readonly List<AIFunction> _mcpTools = new();
    private readonly List<McpClientPrompt> _mcpPrompts = new();

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
                    var transport = new StdioClientTransport(new StdioClientTransportOptions
                    {
                        Name = serverName,
                        Command = serverConfig.Command,
                        Arguments = serverConfig.Args ?? Array.Empty<string>(),
                        EnvironmentVariables = serverConfig.Env ?? new Dictionary<string, string>()
                    });

                    var client = await McpClientFactory.CreateAsync(transport);
                    _mcpClients.Add(client);

                    // Get tools from this client
                    var tools = await client.ListToolsAsync();
                    _mcpTools.AddRange(tools);

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

                    // Show success message for any server that connects (tools and/or prompts)
                    if (showBanners)
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Connected to MCP server: {serverName} ({tools.Count} tools, {promptCount} prompts)[/]");
                    }
                }
                catch (Exception ex)
                {
                    if (showBanners)
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: Failed to connect to MCP server '{serverName}': {ex.Message}[/]");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (showBanners)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: Failed to load MCP configuration: {ex.Message}[/]");
            }
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
        var executableDirectory = Path.GetDirectoryName(executablePath);
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
}

public class McpConfig
{
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();
}