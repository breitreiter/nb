using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

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

        var chatClient = new OpenAI.Chat.ChatClient(model, apiKey);

        return chatClient.AsIChatClient();
    }
}
