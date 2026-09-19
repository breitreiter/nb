using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

public class AzureOpenAIProvider : IChatClientProvider
{
    public string Name => "AzureOpenAI";

    public string[] RequiredConfigKeys => new[]
    {
        "Endpoint",
        "ApiKey"
    };

    public bool CanCreate(IConfiguration config)
    {
        return ProviderConfig.HasRequired(config, RequiredConfigKeys);
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var endpoint = config["Endpoint"];
        var apiKey = ProviderConfig.ApiKeyOrPlaceholder(config);
        var deployment = config["ChatDeploymentName"] ?? "o4-mini";

        var endpointUri = new Uri(endpoint!);
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
            endpointUri,
            new AzureKeyCredential(apiKey),
            options);

        return azureClient.GetChatClient(deployment).AsIChatClient();
    }
}
