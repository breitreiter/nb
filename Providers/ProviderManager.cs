using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace nb;

public class ProviderManager
{
    private readonly List<IChatClientProvider> _providers = new();
    private readonly string _providersDirectory = "providers";

    public ProviderManager()
    {
        LoadBuiltInProviders();
        LoadExternalProviders();
    }

    private void LoadBuiltInProviders()
    {
        _providers.Add(new AzureOpenAIProvider());
    }

    private void LoadExternalProviders()
    {
        if (!Directory.Exists(_providersDirectory))
            return;

        var providerDirs = Directory.GetDirectories(_providersDirectory);
        
        foreach (var providerDir in providerDirs)
        {
            var dirName = Path.GetFileName(providerDir);
            var dllFiles = Directory.GetFiles(providerDir, "*.dll");
            
            foreach (var dllFile in dllFiles)
            {
                try
                {
                    var assembly = Assembly.LoadFrom(dllFile);
                    var providerTypes = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && typeof(IChatClientProvider).IsAssignableFrom(t));

                    foreach (var providerType in providerTypes)
                    {
                        var provider = (IChatClientProvider)Activator.CreateInstance(providerType)!;
                        _providers.Add(provider);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Loaded provider: {provider.Name} from {dirName}/{Path.GetFileName(dllFile)}[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Failed to load provider from {dirName}/{Path.GetFileName(dllFile)}: {ex.Message}[/]");
                }
            }
        }
    }

    public IChatClient? TryCreateChatClient(IConfiguration config)
    {
        var providerType = config["ChatProvider:Type"] ?? "AzureOpenAI";
        
        var provider = _providers.FirstOrDefault(p => 
            string.Equals(p.Name, providerType, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No provider found for type: {providerType}[/]");
            AnsiConsole.MarkupLine($"[dim grey]Available providers: {string.Join(", ", _providers.Select(p => p.Name))}[/]");
            return null;
        }

        if (!provider.CanCreate(config))
        {
            var missingKeys = provider.RequiredConfigKeys
                .Where(key => string.IsNullOrEmpty(config[key]))
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
            return provider.CreateClient(config);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to create client for provider '{provider.Name}': {ex.Message}[/]");
            return null;
        }
    }

    public IEnumerable<string> GetAvailableProviders() => _providers.Select(p => p.Name);
    
    public void ShowProviderStatus(IConfiguration config)
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Available Chat Providers:[/]");
        
        foreach (var provider in _providers)
        {
            var canCreate = provider.CanCreate(config);
            var status = canCreate ? "[green]✓[/]" : "[red]✗[/]";
            AnsiConsole.MarkupLine($"  {status} {provider.Name}");
            
            if (!canCreate)
            {
                var missingKeys = provider.RequiredConfigKeys
                    .Where(key => string.IsNullOrEmpty(config[key]))
                    .ToArray();
                foreach (var key in missingKeys)
                {
                    AnsiConsole.MarkupLine($"[dim grey]    Missing: {key}[/]");
                }
            }
        }
    }
}