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
        // otherwise the SDK talks to api.openai.com directly.
        var chatClient = string.IsNullOrEmpty(endpoint) && http is null
            ? new OpenAI.Chat.ChatClient(model, apiKey)
            : new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey),
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
        return options;
    }
}
