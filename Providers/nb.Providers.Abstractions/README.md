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

## Project Setup

Your provider project needs:

```xml
<PropertyGroup>
  <TargetFramework>net8.0</TargetFramework>
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
