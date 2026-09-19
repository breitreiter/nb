using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Anthropic;
using Anthropic.Core;

namespace nb.Providers;

public class AnthropicProvider : IChatClientProvider
{
    public string Name => "Anthropic";

    public string[] RequiredConfigKeys => new[]
    {
        "ApiKey"
    };

    public bool CanCreate(IConfiguration config)
    {
        return ProviderConfig.HasRequired(config, RequiredConfigKeys);
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var apiKey = ProviderConfig.ApiKeyOrPlaceholder(config);
        var model = config["Model"] ?? "claude-sonnet-4-6";
        var endpoint = config["Endpoint"];

        // An Endpoint points the SDK at a compatible proxy/gateway; the key is then
        // whatever that proxy expects, sent in the SDK's usual x-api-key header.
        var options = new ClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
            options.BaseUrl = endpoint;

        // ClientOptions has no header collection, so extra headers ride on an HttpClient
        // that stamps them. Only set one when the entry asks for headers.
        var http = ProviderConfig.HttpClientWithHeaders(config);
        if (http is not null)
            options.HttpClient = http;

        // Same defect as the OpenAI-family providers, different SDK: Anthropic's
        // ClientOptions.DefaultMaxRetries is 2, so every nb attempt was three requests
        // on the wire that nb never saw. nb owns retry (RetryingChatClient) — budget,
        // jitter, pace and the rate_limited exit reason all live there.
        // bugs/Sdk_Retry_Policy_Multiplies_Every_Model_Call.md
        options.MaxRetries = 0;

        var anthropicClient = new AnthropicClient(options) { ApiKey = apiKey };
        return anthropicClient.AsIChatClient(model);
    }
}
