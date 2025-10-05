using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Mscc.GenerativeAI.Microsoft;

namespace nb.Providers;

public class GeminiProvider : IChatClientProvider
{
    public string Name => "Gemini";

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
        var model = config["Model"] ?? "gemini-2.0-flash-exp";

        var chatClient = new GeminiChatClient(apiKey, model);

        return chatClient;
    }
}
