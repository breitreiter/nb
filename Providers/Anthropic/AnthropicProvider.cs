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

        var anthropicClient = new AnthropicClient(new ClientOptions()) { ApiKey = apiKey };
        return anthropicClient.AsIChatClient(model);
    }
}
