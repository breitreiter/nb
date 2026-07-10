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
        return RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var apiKey = config["ApiKey"]!;
        var model = config["Model"] ?? "claude-sonnet-4-6";
        var endpoint = config["Endpoint"];

        // An Endpoint points the SDK at a compatible proxy/gateway; the key is then
        // whatever that proxy expects, sent in the SDK's usual x-api-key header.
        var options = new ClientOptions();
        if (!string.IsNullOrEmpty(endpoint))
            options.BaseUrl = endpoint;

        var anthropicClient = new AnthropicClient(options) { ApiKey = apiKey };
        return anthropicClient.AsIChatClient(model);
    }
}
