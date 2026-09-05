# nb.Providers.Abstractions

Interface for building AI provider plugins for [NotaBene (nb)](https://github.com/breitreiter/nb).

## Installation

```bash
dotnet add package nb.Providers.Abstractions
```

## Usage

Implement `IChatClientProvider` to create a custom LLM integration:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace nb.Providers;

public class MyProvider : IChatClientProvider
{
    public string Name => "MyProvider";

    public string[] RequiredConfigKeys => new[] { "ApiKey", "Endpoint" };

    public bool CanCreate(IConfiguration config) =>
        RequiredConfigKeys.All(key => !string.IsNullOrEmpty(config[key]));

    public IChatClient CreateClient(IConfiguration config)
    {
        var apiKey = config["ApiKey"];
        var endpoint = config["Endpoint"];
        var model = config["Model"] ?? "default-model";

        // Create your IChatClient implementation here
        // Most LLM SDKs provide Microsoft.Extensions.AI adapters
        return new MyLlmClient(endpoint, apiKey, model);
    }
}
```

## Extra HTTP headers

An entry may carry a `Headers` object for a gateway that authenticates its caller with
its own token:

```jsonc
{ "Name": "GwSonnet", "Provider": "MyProvider",
  "Endpoint": "https://gateway.example.com/...",
  "Headers": { "cf-aig-authorization": "Bearer ${CF_AIG_TOKEN}" } }
```

`ProviderConfig` reads them for you. It hands back an `HttpClient` that stamps them on
every request — pass it to whatever HTTP hook your SDK exposes — or `null` when the
entry declares none, so an unconfigured entry keeps the SDK's default stack:

```csharp
public bool CanCreate(IConfiguration config) =>
    ProviderConfig.HasRequired(config, RequiredConfigKeys);   // excuses ApiKey when Headers carry the credential

public IChatClient CreateClient(IConfiguration config)
{
    var http = ProviderConfig.HttpClientWithHeaders(config);
    var apiKey = ProviderConfig.ApiKeyOrPlaceholder(config);
    ...
}
```

`${VAR}` in a header value is expanded by nb's config layer before your provider sees
it, so there is nothing to resolve yourself.

## Project Setup

Your provider project needs:

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <AssemblyName>MyProvider</AssemblyName>
  <RootNamespace>nb.Providers</RootNamespace>
  <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="nb.Providers.Abstractions" Version="1.0.0" />
  <PackageReference Include="Microsoft.Extensions.AI" Version="9.9.1" />
  <!-- Your LLM SDK package -->
</ItemGroup>
```

Key settings:
- `RootNamespace` must be `nb.Providers`
- `CopyLocalLockFileAssemblies` bundles dependencies with your DLL

## Deployment

1. Build your provider: `dotnet build -c Release`
2. Copy all output DLLs to `nb/bin/.../providers/myprovider/`
3. Add configuration to `appsettings.json`:

```json
{
  "ActiveProvider": "MyProvider",
  "ChatProviders": [
    {
      "Name": "MyProvider",
      "ApiKey": "your-api-key",
      "Endpoint": "https://api.example.com"
    }
  ]
}
```

4. Restart nb

## Documentation

See the [main repository](https://github.com/breitreiter/nb) for full documentation and example provider implementations.
