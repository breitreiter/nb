using Azure;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb;

public class AzureOpenAIProvider : IChatClientProvider
{
    public string Name => "AzureOpenAI";

    public string[] RequiredConfigKeys => new[]
    {
        "ChatProvider:AzureOpenAI:Endpoint",
        "ChatProvider:AzureOpenAI:ApiKey"
    };

    public bool CanCreate(IConfiguration config)
    {
        return RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));
    }

    public IChatClient CreateClient(IConfiguration config)
    {
        var endpoint = config["ChatProvider:AzureOpenAI:Endpoint"];
        var apiKey = config["ChatProvider:AzureOpenAI:ApiKey"];
        var deployment = config["ChatProvider:AzureOpenAI:ChatDeploymentName"] ?? "o4-mini";

        var endpointUri = new Uri(endpoint!);
        var options = new AzureOpenAIClientOptions(AzureOpenAIClientOptions.ServiceVersion.V2025_03_01_Preview);

        var azureClient = new AzureOpenAIClient(
            endpointUri,
            new AzureKeyCredential(apiKey!),
            options);

        return azureClient.GetChatClient(deployment).AsIChatClient();
    }
}