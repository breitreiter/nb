using System.Runtime.Loader;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Utilities;

namespace nb.Providers;

public class ProviderManager
{
    private readonly List<IChatClientProvider> _providers = new();
    private readonly string _providersDirectory;

    // A library host's AppContext.BaseDirectory is its own output dir, which has no
    // providers/ — so the directory is injectable (from NbOptions/config) and only
    // defaults to the executable-relative path for the CLI. See
    // plans/composable-cli-reorientation.md (Phase 6b).
    public ProviderManager(string? providersDirectory = null)
    {
        _providersDirectory = providersDirectory ?? Path.Combine(AppContext.BaseDirectory, "providers");
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
                catch (Exception ex)
                {
                    // Surface the failure (to the facade's redirected sink) so a host can
                    // see why a provider it expected isn't available — was silent before.
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]provider load skipped: {Markup.Escape(Path.GetFileName(dllFile))} — {Markup.Escape(ex.Message)}[/]");
                }
            }
        }
    }

    public IChatClient? TryCreateChatClient(IConfiguration config, string? specificProviderName = null)
    {
        var activeProviderName = specificProviderName ?? config["ActiveProvider"];
        var entries = ProviderEntries.ReadAll(config);

        if (string.IsNullOrEmpty(activeProviderName))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No active provider specified in configuration (ActiveProvider)[/]");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Configured entries: {FormatList(entries.Select(e => e.Label))}[/]");
            return null;
        }

        WarnOnDuplicateLabels(entries);

        // The config entry is what the user selects, so resolve it first -- the
        // implementation behind it is an implementation detail of the entry.
        var entry = ProviderEntries.Find(entries, activeProviderName);

        if (entry == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]No configuration found for provider '{activeProviderName}' in ChatProviders array[/]");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Configured entries: {FormatList(entries.Select(e => e.Label))}[/]");
            return null;
        }

        var providerConfig = entry.Config;
        var provider = _providers.FirstOrDefault(p =>
            string.Equals(p.Name, entry.Implementation, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            // Name the field that actually failed. Reporting only the entry name reads as
            // "the DLL didn't load" and sends you inspecting providers/ for a while.
            if (entry.IsAliased)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Entry '{entry.Label}' names provider implementation '{entry.Implementation}' (via \"{ProviderEntries.ImplementationKey}\"), which is not loaded[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Entry '{entry.Label}' names no known provider implementation[/]");
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Add a \"{ProviderEntries.ImplementationKey}\" field naming one, or rename the entry to match.[/]");
            }

            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Loaded implementations: {FormatList(_providers.Select(p => p.Name))}[/]");
            return null;
        }

        if (!provider.CanCreate(providerConfig))
        {
            var missingKeys = provider.RequiredConfigKeys
                .Where(key => string.IsNullOrEmpty(providerConfig[key]))
                .ToArray();

            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Entry '{entry.Label}' is missing configuration required by '{provider.Name}':[/]");
            foreach (var key in missingKeys)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  - {key}[/]");
            }
            return null;
        }

        try
        {
            // Every caller reaches a client through here (facade, REPL, mid-run
            // provider swaps), so this is the one place retry has to be applied.
            return RetryingChatClient.Wrap(provider.CreateClient(providerConfig), config, providerConfig);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to create client for '{entry.Label}': {Markup.Escape(ex.Message)}[/]");
            return null;
        }
    }

    public IEnumerable<string> GetAvailableProviders() => _providers.Select(p => p.Name);

    /// <summary>
    /// Entry labels that are actually selectable — those whose implementation is loaded.
    /// </summary>
    public IEnumerable<string> GetConfiguredProviders(IConfiguration config) =>
        ProviderEntries.ReadAll(config)
            .Where(e => FindImplementation(e) != null)
            .Select(e => e.Label);

    public void ShowProvidersWithStatus(IConfiguration config, string currentProviderName)
    {
        var entries = ProviderEntries.ReadAll(config);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Available Providers:[/]");

        foreach (var entry in entries)
        {
            var provider = FindImplementation(entry);
            var canCreate = provider != null && provider.CanCreate(entry.Config);
            var isActive = string.Equals(entry.Label, currentProviderName, StringComparison.OrdinalIgnoreCase);

            var status = canCreate ? $"[{UIColors.SpectreSuccess}]configured[/]" : $"[{UIColors.SpectreMuted}]not configured[/]";
            var activeMarker = isActive ? $"[{UIColors.SpectreWarning}]*[/] " : "  ";
            var via = entry.IsAliased ? $"[{UIColors.SpectreMuted}] (via {entry.Implementation})[/]" : string.Empty;

            AnsiConsole.MarkupLine($"{activeMarker}[{UIColors.SpectreInfo}]{entry.Label}[/]{via} {status}");
        }

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]* = active provider[/]");
    }

    public void ShowProviderStatus(IConfiguration config)
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]Configured Chat Providers:[/]");

        var entries = ProviderEntries.ReadAll(config);

        if (entries.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  (none — ChatProviders is empty)[/]");
        }

        foreach (var entry in entries)
        {
            var provider = FindImplementation(entry);
            var canCreate = provider != null && provider.CanCreate(entry.Config);
            var status = canCreate ? $"[{UIColors.SpectreSuccess}]✓[/]" : $"[{UIColors.SpectreError}]✗[/]";
            var via = entry.IsAliased ? $" (via {entry.Implementation})" : string.Empty;

            AnsiConsole.MarkupLine($"  {status} {entry.Label}{via}");

            if (provider == null)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]    No provider implementation named '{entry.Implementation}' is loaded[/]");
            }
            else if (!canCreate)
            {
                var missingKeys = provider.RequiredConfigKeys
                    .Where(key => string.IsNullOrEmpty(entry.Config[key]))
                    .ToArray();
                foreach (var key in missingKeys)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]    Missing: {key}[/]");
                }
            }
        }

        // Implementations nothing points at. Without this the list above can't tell you
        // what a "Provider" field is allowed to say.
        var unused = _providers
            .Select(p => p.Name)
            .Where(name => !entries.Any(e => string.Equals(e.Implementation, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unused.Count > 0)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Loaded but unused implementations: {string.Join(", ", unused)}[/]");
        }
    }

    private IChatClientProvider? FindImplementation(ProviderEntry entry) =>
        _providers.FirstOrDefault(p =>
            string.Equals(p.Name, entry.Implementation, StringComparison.OrdinalIgnoreCase));

    private static void WarnOnDuplicateLabels(List<ProviderEntry> entries)
    {
        foreach (var label in ProviderEntries.DuplicateLabels(entries))
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Warning: more than one ChatProviders entry is named '{label}'; only the first is used[/]");
        }
    }

    private static string FormatList(IEnumerable<string> names)
    {
        var sorted = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return sorted.Count > 0 ? string.Join(", ", sorted) : "(none)";
    }
}