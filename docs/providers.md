# Providers

nb has no built-in providers. Every provider is a plugin loaded at runtime from
`providers/` next to the binary, each in its own `AssemblyLoadContext`. All shipped
providers are compiled into `bin/{Config}/net10.0/providers/` during a build.

## Shipped providers

- **AzureOpenAI**: Chat Completions on classic Azure OpenAI resources.
- **AzureFoundry**: Responses API on classic Azure OpenAI resources. Needed for
  codex-family models such as `gpt-5-codex`, and any other Responses-API-only model.
- **OpenAI**: the direct OpenAI API.
- **Anthropic**: Claude models with function calling.
- **Google Gemini**: Google's generative AI models.
- **LocalLlm**: local servers on the OpenAI wire.
- **Mock**: a testing provider with no API key. See [`testing.md`](testing.md).

## Selecting a provider

A program selects with the `provider` and `model` directives, and can switch between
runs within one document. Connection details (endpoint and key) stay in config; only
the model name travels in the program.

Provider entries are labels, not implementations. An entry's `Name` is free-form. The
optional `Provider` field names the implementation behind it, so several entries can
share one:

```jsonc
{ "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1", "Model": "qwen3-coder-next" },
{ "Name": "LocalAir",   "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8082/v1", "Model": "glm-4.5-air" }
```

Omit `Provider` and it defaults to `Name`.

Per-entry fields worth knowing:

- `Harness`: the costume a run wears when its program names no `harness`. Pair each
  entry with its vendor's costume (`claude-code` for Anthropic, `codex` for OpenAI and
  Azure, `qwen-code` for Qwen). A run that resolves no harness is refused.
- `MaxContextTokens`: set when a model's context window is non-standard.
- `Temperature`: sent on every call for that entry, including the oracle's side call
  when the entry is the judge. Leave it unset on an entry for a Claude 5 model; those
  models reject the parameter.

## Routing through an authenticated gateway

`Endpoint` points an entry at a proxy, and `Headers` supplies whatever that proxy needs
to authenticate you: a Cloudflare AI Gateway, a corporate LLM proxy, anything that
checks its own bearer token before forwarding upstream.

```jsonc
{ "Name": "GwSonnet", "Provider": "Anthropic",
  "Endpoint": "https://gateway.example.com/account/gw/anthropic",
  "ApiKey": "${ANTHROPIC_API_KEY}",
  "Headers": { "cf-aig-authorization": "Bearer ${CF_AIG_TOKEN}" },
  "Model": "claude-sonnet-5" }
```

Every header is sent on every request alongside the provider SDK's own auth header, or
instead of it if you name the same header, since a configured value replaces the SDK's.
Values are ordinary config values, so `${VAR}` is expanded at startup and the token
never has to live in the file.

If the gateway holds the upstream key itself (stored-keys or BYOK mode), leave `ApiKey`
out. An entry that carries `Headers` does not need one.

Supported on `Anthropic`, `OpenAI`, `LocalLlm`, `AzureOpenAI`, and `AzureFoundry`. Not
on `Gemini`, whose SDK accepts neither a custom base URL nor a custom HTTP stack.

## Which Azure provider do I want?

Match the API shape your deployment exposes:

| Your deployment URL looks like | Use |
|---|---|
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/deployments/<name>/chat/completions?...` | `AzureOpenAI` |
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/responses?...` | `AzureFoundry` |

Both accept either the resource root or the full deployment URL in `Endpoint`; the
plugin strips to the host. The `Model` field is your deployment name, not the model
family name. If Azure shows an endpoint on `services.ai.azure.com` with a
`/api/projects/<project>/...` path, that is the newer Foundry Unified Endpoint and
neither provider targets it directly. Open an issue if you need that variant.

## `EditToolStyle` (deprecated)

A per-entry field that selects the file-edit surface: `EditReplace` (default)
advertises `edit_file` and `write_file`; `ApplyPatch` advertises `apply_patch` instead.
They are mutually exclusive because GPT-family models confuse the two when both are
present.

It still works, and setting it prints a warning. Use the `harness codex` program
directive instead. `apply_patch` is the Codex edit surface, and the costume brings the
rest of that surface with it: shell-only file access, the plan tool, `AGENTS.md`, the
environment block. If you set `EditReplace`, remove the field; that is the default. A
program that names a `harness` overrides this field either way.

## Writing your own provider

1. Create a project and add the abstractions package:
   ```bash
   dotnet add package nb.Providers.Abstractions
   ```
2. Implement `IChatClientProvider`: supply an `IChatClient` from
   Microsoft.Extensions.AI plus basic configuration wiring.
3. Build and copy the assembly to a new subdirectory under `providers/`.
4. Add the entry to `appsettings.json`.

See [nb.Providers.Abstractions](https://www.nuget.org/packages/nb.Providers.Abstractions)
for the interface documentation and examples. The interface is a versioned public
contract with out-of-tree implementers; additions to it keep default implementations.
