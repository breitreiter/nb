using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Anthropic.SDK;

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
            var apiKey = config["ApiKey"];
            var model = config["Model"] ?? Anthropic.SDK.Constants.AnthropicModels.Claude37Sonnet;

            var anthropicClient = new AnthropicClient(apiKey);

            // Return the Messages endpoint as IChatClient
            // Use ChatClientBuilder to add function invocation support
            return new ChatClientBuilder(anthropicClient.Messages)
                .UseFunctionInvocation()
                .Build();
        }
    }
