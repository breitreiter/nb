using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace nb.Providers;

public class OpenAIProvider : IChatClientProvider
{
    public string Name => "OpenAI";

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
        var model = config["Model"] ?? "gpt-4o-mini";
        var endpoint = config["Endpoint"];
        var http = ProviderConfig.HttpClientWithHeaders(config);

        // An Endpoint routes the OpenAI dialect through a compatible proxy/gateway;
        // otherwise the SDK talks to api.openai.com directly. Always through
        // OpenAIOptions, even with neither: the options-less constructor would keep the
        // SDK's default retry policy, which is the whole point of building options.
        var chatClient = new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey),
            OpenAIOptions(endpoint, http));

        return chatClient.AsIChatClient();
    }

    // Extra headers ride on the transport rather than a policy: OpenAI's own
    // header-setting policy type is internal, and the transport hook takes a plain
    // HttpClient, which is the same mechanism every other provider here uses.
    private static OpenAIClientOptions OpenAIOptions(string? endpoint, HttpClient? http)
    {
        var options = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
            options.Endpoint = new Uri(endpoint);
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
        return options;
    }
}
