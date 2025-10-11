using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Utilities;

namespace nb.Providers;

public class ProviderManager
{
    private readonly List<IChatClientProvider> _providers = new();
    private readonly string _providersDirectory = "providers";

    public ProviderManager()
    {
        LoadExternalProviders();
    }

    private void LoadExternalProviders()
    {
        if (!Directory.Exists(_providersDirectory))
            return;

        var providerDirs = Directory.GetDirectories(_providersDirectory);

        foreach (var providerDir in providerDirs)
        {
            var dirName = Path.GetFileName(providerDir);
            var dllFiles = Directory.GetFiles(providerDir, "*.dll")
                .Where(f => !f.EndsWith("nb.Providers.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var dllFile in dllFiles)
            {
                try
                {
                    // Create isolated AssemblyLoadContext for this provider
                    var loadContext = new AssemblyLoadContext($"Provider_{dirName}_{Path.GetFileNameWithoutExtension(dllFile)}", isCollectible: false);

                    // Handle assembly resolution for dependencies in the provider directory
                    loadContext.Resolving += (context, assemblyName) =>
                    {
                        var assemblyPath = Path.GetFullPath(Path.Combine(providerDir, assemblyName.Name + ".dll"));
                        if (File.Exists(assemblyPath))
                        {
                            return context.LoadFromAssemblyPath(assemblyPath);
                        }
                        return null;
                    };

                    var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(dllFile));
                    var providerTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && typeof(IChatClientProvider).IsAssignableFrom(t));

                    foreach (var providerType in providerTypes)
                    {
                        var provider = (IChatClientProvider)Activator.CreateInstance(providerType)!;
                        _providers.Add(provider);
                    }
                }
                catch (Exception)
                {
                    // Silently skip failed providers
                }
            }
        }
    }

    public IChatClient? TryCreateChatClient(IConfiguration config, string? specificProviderName = null)
    {
        var activeProviderName = specificProviderName ?? config["ActiveProvider"];

        if (string.IsNullOrEmpty(activeProviderName))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No active provider specified in configuration (ActiveProvider)[/]");
            AnsiConsole.MarkupLine($"[dim grey]Available providers: {string.Join(", ", _providers.Select(p => p.Name))}[/]");
            return null;
        }

        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Name, activeProviderName, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No provider found for: {activeProviderName}[/]");
            AnsiConsole.MarkupLine($"[dim grey]Available providers: {string.Join(", ", _providers.Select(p => p.Name))}[/]");
            return null;
        }

        // Find the provider's configuration from the ChatProviders array
        var providerConfigs = config.GetSection("ChatProviders").GetChildren();
        var providerConfig = providerConfigs.FirstOrDefault(c =>
            string.Equals(c["Name"], activeProviderName, StringComparison.OrdinalIgnoreCase));

        if (providerConfig == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No configuration found for provider '{activeProviderName}' in ChatProviders array[/]");
            return null;
        }

        if (!provider.CanCreate(providerConfig))
        {
            var missingKeys = provider.RequiredConfigKeys
                .Where(key => string.IsNullOrEmpty(providerConfig[key]))
                .ToArray();

            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Provider '{provider.Name}' is missing required configuration:[/]");
            foreach (var key in missingKeys)
            {
                AnsiConsole.MarkupLine($"[dim grey]  - {key}[/]");
            }
            return null;
        }

        try
        {
            return provider.CreateClient(providerConfig);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to create client for provider '{provider.Name}': {Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    public IEnumerable<string> GetAvailableProviders() => _providers.Select(p => p.Name);

    public IEnumerable<string> GetConfiguredProviders(IConfiguration config)
    {
        var providerConfigs = config.GetSection("ChatProviders").GetChildren();

        return _providers
            .Where(provider =>
            {
                var providerConfig = providerConfigs.FirstOrDefault(c =>
                    string.Equals(c["Name"], provider.Name, StringComparison.OrdinalIgnoreCase));
                return providerConfig != null;
            })
            .Select(p => p.Name);
    }

    public void ShowProvidersWithStatus(IConfiguration config, string currentProviderName)
    {
        var providerConfigs = config.GetSection("ChatProviders").GetChildren();

        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Available Providers:[/]");

        foreach (var provider in _providers)
        {
            var providerConfig = providerConfigs.FirstOrDefault(c =>
                string.Equals(c["Name"], provider.Name, StringComparison.OrdinalIgnoreCase));

            var canCreate = providerConfig != null && provider.CanCreate(providerConfig);
            var isActive = string.Equals(provider.Name, currentProviderName, StringComparison.OrdinalIgnoreCase);

            var status = canCreate ? "[green]configured[/]" : "[dim grey]not configured[/]";
            var activeMarker = isActive ? "[yellow]*[/] " : "  ";

            AnsiConsole.MarkupLine($"{activeMarker}[{UIColors.SpectreInfo}]{provider.Name}[/] {status}");
        }

        AnsiConsole.MarkupLine($"[dim grey]* = active provider[/]");
    }

    public void ShowProviderStatus(IConfiguration config)
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Available Chat Providers:[/]");

        var providerConfigs = config.GetSection("ChatProviders").GetChildren();

        foreach (var provider in _providers)
        {
            var providerConfig = providerConfigs.FirstOrDefault(c =>
                string.Equals(c["Name"], provider.Name, StringComparison.OrdinalIgnoreCase));

            var canCreate = providerConfig != null && provider.CanCreate(providerConfig);
            var status = canCreate ? "[green]✓[/]" : "[red]✗[/]";
            AnsiConsole.MarkupLine($"  {status} {provider.Name}");

            if (providerConfig == null)
            {
                AnsiConsole.MarkupLine($"[dim grey]    No configuration found in ChatProviders array[/]");
            }
            else if (!canCreate)
            {
                var missingKeys = provider.RequiredConfigKeys
                    .Where(key => string.IsNullOrEmpty(providerConfig[key]))
                    .ToArray();
                foreach (var key in missingKeys)
                {
                    AnsiConsole.MarkupLine($"[dim grey]    Missing: {key}[/]");
                }
            }
        }
    }
}