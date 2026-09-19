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
        return ProviderConfig.HasRequired(config, RequiredConfigKeys);
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var endpoint = config["Endpoint"]!;
        var apiKey = ProviderConfig.ApiKeyOrPlaceholder(config);
        var model = config["Model"]!;

        // Accept either the resource root or the full deployment URL — the Azure SDK
        // wants the base onto which it appends "openai/responses". Strip that suffix
        // but KEEP any leading path, so a gateway that adds one still routes.
        var parsed = new Uri(endpoint);
        var idx = parsed.AbsolutePath.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        var basePath = idx >= 0 ? parsed.AbsolutePath[..idx] : parsed.AbsolutePath.TrimEnd('/');
        var baseUri = new Uri($"{parsed.Scheme}://{parsed.Authority}{basePath}/");

        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        var http = ProviderConfig.HttpClientWithHeaders(config);
        if (http is not null)
            options.Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(http);

        // nb owns retry, and owns it better: the wall-clock budget, half-jitter,
        // adaptive pace and the rate_limited exit reason all live in
        // RetryingChatClient. The SDK's own policy sits *below* AsIChatClient(), so it
        // runs to completion before nb is handed an exception — nb never learns those
        // requests happened, and a measured run put four on the wire per nb attempt.
        // Zero, not one: two stacked retry layers multiply request count and divide
        // nb's visibility rather than compounding resilience.
        // bugs/Sdk_Retry_Policy_Multiplies_Every_Model_Call.md
        options.RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0);

        var azureClient = new AzureOpenAIClient(
            baseUri,
            new AzureKeyCredential(apiKey),
            options);

        return azureClient.GetResponsesClient().AsIChatClient(model);
    }
}
