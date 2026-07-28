using Microsoft.Extensions.Configuration;

namespace nb;

/// <summary>
/// Pure config resolution for the active provider's engine knobs — shared by the
/// facade's <see cref="NbRuntime"/> and the CLI's inline wiring. Lives in nb.Core so
/// there's no engine→CLI dependency (these were on <c>Program</c> before the split).
/// </summary>
internal static class ProviderConfigResolver
{
    public static float? ResolveProviderFloat(IConfiguration config, string providerName, string key)
    {
        foreach (var provider in config.GetSection("ChatProviders").GetChildren())
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
                return float.TryParse(provider[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return null;
    }

    public static int ResolveMaxContextTokens(IConfiguration config, string providerName)
    {
        var providers = config.GetSection("ChatProviders").GetChildren();
        foreach (var provider in providers)
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (int.TryParse(provider["MaxContextTokens"], out var providerTokens))
                    return providerTokens;
                break;
            }
        }
        return int.TryParse(config["MaxContextTokens"], out var tokens) ? tokens : 128000;
    }

    // Write both keys: most providers read "Model", but classic AzureOpenAI reads
    // "ChatDeploymentName". Setting both lets a program's `model` directive land
    // whichever field the provider reads, without knowing the provider kind.
    public static void OverrideProviderModel(IConfiguration config, string providerName, string model)
    {
        var children = config.GetSection("ChatProviders").GetChildren().ToList();
        for (int i = 0; i < children.Count; i++)
            if (string.Equals(children[i]["Name"], providerName, StringComparison.OrdinalIgnoreCase))
            {
                config[$"ChatProviders:{i}:Model"] = model;
                config[$"ChatProviders:{i}:ChatDeploymentName"] = model;
                return;
            }
    }
}
