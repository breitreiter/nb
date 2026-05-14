using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

namespace nb.Providers;

/// <summary>
/// Provider for OpenAI-compatible local inference servers (Ollama, llama.cpp,
/// vLLM, LM Studio, koboldcpp, etc.) reachable over the network.
/// Reuses the OpenAI .NET SDK but redirects the base endpoint.
/// </summary>
public class LocalLlmProvider : IChatClientProvider
{
    public string Name => "LocalLlm";

    public string[] RequiredConfigKeys => new[]
    {
        "Endpoint",
        "Model"
    };

    public bool CanCreate(IConfiguration config)
    {
        return RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var endpoint = config["Endpoint"]!;
        var model = config["Model"]!;
        // Most local servers ignore the key but the SDK requires non-empty.
        var apiKey = string.IsNullOrEmpty(config["ApiKey"]) ? "local" : config["ApiKey"]!;

        var options = new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
        var chatClient = new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey), options);

        return chatClient.AsIChatClient();
    }
}
