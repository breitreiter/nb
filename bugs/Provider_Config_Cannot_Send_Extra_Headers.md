# Provider entries can't send extra HTTP headers, so an authenticated gateway is unreachable

Status: Open (2026-08-13) — found pointing nb at a hosted LLM gateway to reach
Sonnet and the GPT-5 family through one observability plane. Against `97087dd`.
**Severity: medium** — one gateway mode works, and it is the mode that requires
holding every upstream key locally.

## Symptom

`Endpoint` is enough to route a provider through a proxy, and that much works:

```jsonc
// works — gateway with authentication disabled, upstream key in the request
{ "Name": "GwSonnet", "Provider": "Anthropic",
  "Endpoint": "https://<gateway-host>/<account>/<gateway>/anthropic",
  "ApiKey": "${ANTHROPIC_API_KEY}", "Model": "claude-sonnet-5" }
```

There is no config key that adds a header, so the same gateway with its
authentication turned on cannot be described at all. It wants

```
cf-aig-authorization: Bearer <gateway-token>
```

*alongside* the upstream key, and nb has nowhere to put it. The request goes out
without the header and the gateway rejects it before reaching the provider.

## Mechanism

`IChatClientProvider.CreateClient(IConfiguration config)` is the only hook a
provider gets, and both implementations spend the single `ApiKey` on the SDK's
own auth header and read nothing else:

- `Providers/Anthropic/AnthropicProvider.cs:30` builds a `ClientOptions`
  carrying only `BaseUrl`, then hands `ApiKey` to the client → `x-api-key`.
- `Providers/OpenAI/OpenAIProvider.cs:32` builds `OpenAIClientOptions` carrying
  only `Endpoint`, plus an `ApiKeyCredential` → `Authorization: Bearer`.

Neither reads a header map, and the interface has no place to declare one. So a
proxy that needs a *second* header, or a *different* header, is not expressible.

Second-order: `CanCreate` requires `ApiKey` to be non-empty
(`AnthropicProvider.cs:19`, `OpenAIProvider.cs:19`). A gateway that holds the
upstream key itself needs no `ApiKey` at all, so even once headers exist that
mode would need a dummy value to satisfy the check.

## What it costs

A hosted gateway typically offers three auth modes. nb can reach one.

| mode | header requirement | nb |
|---|---|---|
| unauthenticated, upstream key in request | the SDK's own auth header | ✅ works today |
| authenticated gateway (opt-in) | gateway token **plus** upstream key | ❌ |
| stored keys / BYOK / unified billing | gateway token **instead of** upstream key | ❌ |

The third row is the one worth caring about: it is the mode where the upstream
key never leaves the gateway, which is the stronger posture. nb is restricted to
the mode that keeps every provider key on the box — so routing through a gateway
currently adds observability without subtracting secret sprawl.

This is not one vendor's quirk. Any corporate LLM proxy that authenticates
callers with its own bearer token has the same shape, and that is the common
deployment for a team that wants per-user attribution.

## Precedent: MCP already has exactly this

`McpServerConfig.Headers` (`nb.Core/MCP/McpManager.cs:429`) is a
`Dictionary<string,string>` resolved through `McpManager.ResolveHeaders`
(`:215`), which interpolates `${VAR}` so tokens stay out of the committed file
and warns on an unset variable. The same key on a provider entry:

```jsonc
{
  "Name": "GwSonnet",
  "Provider": "Anthropic",
  "Endpoint": "https://<gateway-host>/<account>/<gateway>/anthropic",
  "ApiKey": "${ANTHROPIC_API_KEY}",
  "Headers": { "cf-aig-authorization": "Bearer ${CF_AIG_TOKEN}" },
  "Model": "claude-sonnet-5"
}
```

## Fix

Both SDKs have a hook, but they are different hooks — checked against the
shipped assemblies rather than guessed:

- **Anthropic 12.16.0** — `ClientOptions` exposes a read-only `Headers`
  collection (`get_Headers`, with an internal `AddDefaultHeaders`). Populate it
  before constructing `AnthropicClient`.
- **OpenAI 2.10.0** — no headers property. Add a per-call policy instead:
  `OpenAIClientOptions.AddPolicy(...)` (inherited from
  `ClientPipelineOptions`) with a `GenericActionPipelinePolicy`, which ships in
  the OpenAI package, setting the header on `message.Request.Headers`.

Two mechanisms for one config key argues for resolving `${VAR}` once in
`nb.Core` — reusing `ResolveHeaders` rather than duplicating it — and handing
each provider an already-resolved `IDictionary<string,string>`.

That means touching `IChatClientProvider`, which is the **published plugin
interface**; `plans/provider-owned-tools.md` is explicit about out-of-tree
implementers. Add an optional overload (or a separate small interface a provider
may implement) rather than changing the existing signature, so a third-party
provider built against today's contract keeps compiling.

Worth deciding at the same time: whether `RequiredConfigKeys` should stop
demanding `ApiKey` when `Headers` carries the credential, or whether the
gateway-holds-the-key case is better served by allowing an empty `ApiKey`.

## Test

No live gateway needed — the assertion is about the outgoing request:

```
Headers = { "x-test": "Bearer ${TEST_TOKEN}" }   # with TEST_TOKEN set
```

Point `Endpoint` at a local listener and assert the header arrives interpolated,
once, on both providers. Then assert an unset `${VAR}` warns and resolves empty,
matching `ResolveHeaders` semantics rather than inventing new ones.
