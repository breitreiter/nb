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
        var apiKey = config["ApiKey"] ?? throw new InvalidOperationException("ApiKey is required for Gemini provider");
        var model = config["Model"] ?? "gemini-2.0-flash-exp";

        // Note: no Endpoint override — the Mscc SDK ignores a custom base URL, so
        // Gemini can't be routed through a proxy. It talks to Google directly.
        var chatClient = new GeminiChatClient(apiKey: apiKey, model: model);

        return chatClient;
    }
}
