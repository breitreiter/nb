using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

/// <summary>
/// Provider for Azure-hosted models that use the OpenAI Responses API
/// (e.g., gpt-5-codex family). Works against classic Azure OpenAI resources
/// on *.cognitiveservices.azure.com — the same resource type the AzureOpenAI
/// plugin uses, but calling the Responses API instead of Chat Completions.
/// </summary>
public class AzureFoundryProvider : IChatClientProvider
{
    public string Name => "AzureFoundry";

    public string[] RequiredConfigKeys => new[]
    {
        "Endpoint",
        "ApiKey",
        "Model"
    };

    public bool CanCreate(IConfiguration config)
    {
        return RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var endpoint = config["Endpoint"]!;
        var apiKey = config["ApiKey"]!;
        var model = config["Model"]!;

        // Accept either the resource root or the full deployment URL — the Azure SDK
        // wants the base onto which it appends "openai/responses". Strip that suffix
        // but KEEP any leading path, so a gateway that adds one still routes.
        var parsed = new Uri(endpoint);
        var idx = parsed.AbsolutePath.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        var basePath = idx >= 0 ? parsed.AbsolutePath[..idx] : parsed.AbsolutePath.TrimEnd('/');
        var baseUri = new Uri($"{parsed.Scheme}://{parsed.Authority}{basePath}/");

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);
        var azureClient = new AzureOpenAIClient(
            baseUri,
            new AzureKeyCredential(apiKey),
            options);

        return azureClient.GetResponsesClient().AsIChatClient(model);
    }
}
