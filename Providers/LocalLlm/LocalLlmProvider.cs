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

        // A gateway in front of the OpenAI dialect wants its own token; the transport
        // hook is where a plain HttpClient can stamp it.
        var http = ProviderConfig.HttpClientWithHeaders(config);
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

        var chatClient = new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(apiKey), options);

        return chatClient.AsIChatClient();
    }
}
