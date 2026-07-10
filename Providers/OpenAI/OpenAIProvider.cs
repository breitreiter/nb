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
        return RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var apiKey = config["ApiKey"];
        var model = config["Model"] ?? "gpt-4o-mini";
        var endpoint = config["Endpoint"];

        // An Endpoint routes the OpenAI dialect through a compatible proxy/gateway;
        // otherwise the SDK talks to api.openai.com directly.
        var chatClient = string.IsNullOrEmpty(endpoint)
            ? new OpenAI.Chat.ChatClient(model, apiKey)
            : new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey!),
                new OpenAIClientOptions { Endpoint = new Uri(endpoint) });

        return chatClient.AsIChatClient();
    }
}
