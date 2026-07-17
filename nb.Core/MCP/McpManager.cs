using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Spectre.Console;
using nb.Utilities;
using ModelContextProtocol.Protocol;

namespace nb.MCP;

public class McpManager : IDisposable
{
    private readonly List<McpClient> _mcpClients = new();
    private readonly List<AIFunction> _mcpTools = new();
    private readonly List<ResourceInfo> _mcpResources = new();
    private readonly List<string> _connectedServerNames = new();
    private readonly Dictionary<string, List<AIFunction>> _toolsByServer = new();
    private readonly HashSet<string> _alwaysAllowTools = new();
    private McpConfig _config = new();

    /// <summary>
    /// Reads the MCP manifest and caches the config. No network connections are
    /// made. With no <paramref name="manifestPath"/>, mcp.json resolves in layers
    /// (install -> user -> project, later winning by server name); --mcp passes an
    /// explicit path for a hermetic per-invocation manifest.
    /// </summary>
    public void LoadConfig(string? manifestPath = null)
    {
        try { _config = LoadMcpConfiguration(manifestPath); }
        catch { _config = new McpConfig(); }
    }

    /// <summary>
    /// Connects any of the named servers that are not yet connected.
    /// Safe to call multiple times — already-connected servers are skipped.
    /// </summary>
    public async Task EnsureServersConnectedAsync(IEnumerable<string> serverNames)
    {
        var servers = _config.McpServers.Count > 0 ? _config.McpServers : _config.Servers;
        foreach (var name in serverNames)
        {
            if (_connectedServerNames.Contains(name)) continue;
            if (!servers.TryGetValue(name, out var serverConfig))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]MCP: '{name}' not found in mcp.json[/]");
                continue;
            }
            try { await ConnectServerAsync(name, serverConfig); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]MCP error: {name} — {Markup.Escape(ex.Message)}[/]"); }
        }
    }

    /// <summary>
    /// Connects all configured servers. Used by --dump-tools.
    /// </summary>
    public async Task ConnectAllAsync()
    {
        var servers = _config.McpServers.Count > 0 ? _config.McpServers : _config.Servers;
        foreach (var (name, serverConfig) in servers)
        {
            if (_connectedServerNames.Contains(name)) continue;
            try { await ConnectServerAsync(name, serverConfig); }
            catch (Exception ex) { AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]MCP error: {name} — {Markup.Escape(ex.Message)}[/]"); }
        }
    }

    private async Task ConnectServerAsync(string serverName, McpServerConfig serverConfig)
    {
        IClientTransport transport;

        if (serverConfig.Type.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(serverConfig.Endpoint))
                throw new InvalidOperationException($"HTTP transport for '{serverName}' requires an 'endpoint' property");

            transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(serverConfig.Endpoint),
                AdditionalHeaders = ResolveHeaders(serverConfig.Headers)
            });
        }
        else
        {
            var envVars = new Dictionary<string, string?>();
            if (serverConfig.Env != null)
            {
                foreach (var kvp in serverConfig.Env)
                    envVars[kvp.Key] = kvp.Value;
            }

            transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = serverName,
                Command = serverConfig.Command,
                Arguments = serverConfig.Args ?? Array.Empty<string>(),
                EnvironmentVariables = envVars
            });
        }

        // TODO: Centralize version string (also used in startup banner)
        var clientOptions = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "nb", Version = "0.9.0" },
            Capabilities = new ClientCapabilities
            {
                Roots = new RootsCapability { ListChanged = true }
            },
            Handlers = new McpClientHandlers
            {
                RootsHandler = (request, cancellationToken) =>
                {
                    var cwd = Directory.GetCurrentDirectory();
                    var cwdUri = new Uri(cwd).AbsoluteUri;
                    return ValueTask.FromResult(new ListRootsResult
                    {
                        Roots = new List<Root> { new Root { Uri = cwdUri, Name = "Working Directory" } }
                    });
                }
            }
        };

        var client = await McpClient.CreateAsync(transport, clientOptions);
        _mcpClients.Add(client);
        _connectedServerNames.Add(serverName);

        // Expose every tool under its "{server}_{tool}" composite name — the name the
        // model calls and the surface/approval layers key on. _toolsByServer must hold
        // the same prefixed instances so an `mcp +server` directive selects tools under
        // the name the model actually uses (GetToolsForServers).
        var tools = await client.ListToolsAsync();
        var prefixed = tools.Select(t => (AIFunction)t.WithName($"{serverName}_{t.Name}")).ToList();
        _toolsByServer[serverName] = prefixed;
        _mcpTools.AddRange(prefixed);

        if (serverConfig.AlwaysAllow != null)
        {
            // Tools are exposed to the model under their "{server}_{tool}" name
            // (line above), and the model calls — and approval checks — that same
            // composite name. So the allow-list must be keyed by the composite,
            // not the bare tool name. Config entries match the bare name leniently:
            // the SDK normalizes '-' to '_', so "current-time" in the manifest
            // still matches the tool exposed as "current_time".
            bool wildcard = serverConfig.AlwaysAllow.Contains("*");
            var allow = serverConfig.AlwaysAllow
                .Select(NormalizeToolName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var tool in tools)
                if (wildcard || allow.Contains(NormalizeToolName(tool.Name)))
                    _alwaysAllowTools.Add($"{serverName}_{tool.Name}");
        }

        try
        {
            var resources = await client.ListResourcesAsync();
            foreach (var resource in resources)
            {
                _mcpResources.Add(new ResourceInfo
                {
                    ServerName = serverName,
                    Client = client,
                    Uri = resource.Uri,
                    Name = resource.Name ?? resource.Uri,
                    Description = resource.Description,
                    MimeType = resource.MimeType
                });
            }
        }
        catch
        {
            // Many MCP servers don't support resources
        }
    }

    // Resolve ${VAR} references in header values against environment variables, so
    // tokens (e.g. a bearer credential) can live in the environment, never in mcp.json.
    // Matches the appsettings.json convention in ConfigurationService.
    private static readonly System.Text.RegularExpressions.Regex EnvRef =
        new(@"\$\{(\w+)\}");

    internal static IDictionary<string, string>? ResolveHeaders(Dictionary<string, string>? headers)
    {
        if (headers == null || headers.Count == 0) return null;

        return headers.ToDictionary(
            kvp => kvp.Key,
            kvp => EnvRef.Replace(kvp.Value, m =>
            {
                var name = m.Groups[1].Value;
                var resolved = Environment.GetEnvironmentVariable(name);
                if (resolved is null)
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: environment variable '{name}' referenced in mcp.json is not set[/]");
                return resolved ?? "";
            }));
    }

    public IReadOnlyList<AIFunction> GetTools()
    {
        return _mcpTools.AsReadOnly();
    }

    public IReadOnlyList<AIFunction> GetToolsForServers(IEnumerable<string> serverNames)
    {
        var names = new HashSet<string>(serverNames, StringComparer.OrdinalIgnoreCase);
        return _toolsByServer
            .Where(kvp => names.Contains(kvp.Key))
            .SelectMany(kvp => kvp.Value)
            .ToList()
            .AsReadOnly();
    }

    public IReadOnlyList<string> GetConnectedServerNames()
    {
        return _connectedServerNames.AsReadOnly();
    }

    public bool IsAlwaysAllowed(string toolName)
    {
        return _alwaysAllowTools.Contains(toolName);
    }

    // Tool names in mcp.json may use '-' while the SDK exposes them with '_'.
    private static string NormalizeToolName(string name) => name.Replace('-', '_');

    public IReadOnlyList<ResourceInfo> GetResources()
    {
        return _mcpResources.AsReadOnly();
    }

    public object BuildToolManifest()
    {
        var servers = new Dictionary<string, object>();
        foreach (var (serverName, tools) in _toolsByServer)
        {
            servers[serverName] = new
            {
                tools = tools.Select(t =>
                {
                    var entry = new Dictionary<string, object>
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description ?? ""
                    };
                    if (t.JsonSchema.ValueKind != JsonValueKind.Undefined)
                    {
                        entry["inputSchema"] = t.JsonSchema;
                    }
                    return entry;
                }).ToArray()
            };
        }
        return new { servers };
    }

    public async Task<string> ReadResourceAsync(string uri)
    {
        // Find the resource info
        var resourceInfo = _mcpResources.FirstOrDefault(r => r.Uri == uri);
        if (resourceInfo == null)
        {
            throw new InvalidOperationException($"Resource not found: {uri}");
        }

        // Read the resource from the appropriate client
        var result = await resourceInfo.Client.ReadResourceAsync(uri);

        // Return the first resource content (we only request one URI at a time)
        if (result.Contents.Count == 0)
        {
            throw new InvalidOperationException($"Resource returned no content: {uri}");
        }

        var content = result.Contents[0];

        // Handle text and blob content
        if (content is TextResourceContents textContent)
        {
            return textContent.Text ?? throw new InvalidOperationException($"Text resource content is null: {uri}");
        }
        else if (content is BlobResourceContents blobContent)
        {
            // Return base64-encoded blob with mime type info
            return $"[Binary content, MIME type: {content.MimeType ?? "unknown"}]\nBase64: {blobContent.Blob}";
        }
        else
        {
            throw new InvalidOperationException($"Unknown resource content type: {content.GetType().Name}");
        }
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

    // A manifest path (--mcp) is a hermetic single file. Otherwise mcp.json layers
    // like config.json: install (next to the binary) -> user (~/.config/nb) ->
    // nearest project (.nb/mcp.json), later winning by server name — so a project
    // can add or override servers without editing the install manifest.
    private static McpConfig LoadMcpConfiguration(string? manifestPath)
    {
        if (!string.IsNullOrEmpty(manifestPath))
            return ReadMcpFile(manifestPath) ?? new McpConfig();

        var executableDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
            ?? Directory.GetCurrentDirectory();
        return MergeLayers(new[]
        {
            Path.Combine(executableDirectory, "mcp.json"),
            Path.Combine(ConfigurationService.UserNbDir(), "mcp.json"),
            ConfigurationService.FindProjectNbFile(Directory.GetCurrentDirectory(), "mcp.json"),
        });
    }

    // Merge server definitions across ordered layer files, later winning by name.
    // Null/missing paths are skipped. Internal for testing.
    internal static McpConfig MergeLayers(IEnumerable<string?> paths)
    {
        var merged = new McpConfig();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var layer = ReadMcpFile(path);
            if (layer == null) continue;
            foreach (var (name, server) in layer.McpServers) merged.McpServers[name] = server;
            foreach (var (name, server) in layer.Servers) merged.Servers[name] = server;
        }
        return merged;
    }

    private static McpConfig? ReadMcpFile(string path)
    {
        if (!File.Exists(path)) return null;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        return JsonSerializer.Deserialize<McpConfig>(File.ReadAllText(path), options);
    }
}

public class McpServerConfig
{
    public string Type { get; set; } = "stdio"; // "stdio" or "http"
    public string Command { get; set; } = string.Empty;
    public string[]? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public int Timeout { get; set; } = 60000;
    public string[]? AlwaysAllow { get; set; }
    public string Endpoint { get; set; } = string.Empty; // For HTTP transport
    public Dictionary<string, string>? Headers { get; set; } // For HTTP transport; values support ${VAR} env interpolation
}

public class McpConfig
{
    public Dictionary<string, McpServerConfig> McpServers { get; set; } = new();
    public Dictionary<string, McpServerConfig> Servers { get; set; } = new();
}

public class ResourceInfo
{
    public required string ServerName { get; init; }
    public required McpClient Client { get; init; }
    public required string Uri { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? MimeType { get; init; }
}