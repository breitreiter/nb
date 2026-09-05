---
kind: bug
title: 'Provider entries can''t send extra HTTP headers, so an authenticated gateway is unreachable'
created: 2026-08-13
updated: 2026-09-05
status: current
state: fixed
severity: medium
cluster: feature-gap
---

# Provider entries can't send extra HTTP headers, so an authenticated gateway is unreachable

Status: **Fixed 2026-09-05.** Originally: Open (2026-08-13) — found pointing nb at a hosted LLM gateway to reach
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


## Resolution

**Fixed 2026-09-05.** `Headers` is now a key on a `ChatProviders` entry, honoured by
`Anthropic`, `OpenAI`, `LocalLlm`, `AzureOpenAI` and `AzureFoundry`. All three gateway
modes in the table above now work.

### The published interface did not need to change

The report proposed adding an overload to `IChatClientProvider` so nb.Core could hand a
provider an already-resolved header map. That turned out to be unnecessary on both
counts, which is the good outcome: a provider already receives its entry as an
`IConfiguration`, so `Headers` is readable through the hook that exists, and
`ConfigurationService.ExpandEnvironmentReferences` already walks *every* value in the
config root — nested ones included — so `${VAR}` inside a header value is expanded
before a provider sees it. Header values get the same treatment as `ApiKey` because
they are the same kind of thing, rather than by a second copy of `ResolveHeaders`.

`IChatClientProvider` is byte-for-byte unchanged. An out-of-tree provider built against
today's contract keeps compiling and keeps working.

### Two of the report's SDK claims were wrong

Both were checked by reflecting over the shipped assemblies rather than re-reading the
report, and both failed:

- **Anthropic 12.16.0 `ClientOptions` has no `Headers` collection.** It is a `record
  struct` with `ApiKey`, `AuthToken`, `BaseUrl`, `HttpClient`, `MaxRetries`,
  `ResponseValidation` and `Timeout` — nothing else. There is no `get_Headers`.
  `AddDefaultHeaders` *is* a real name in that assembly, which is presumably where the
  claim came from, but it is a public method on the *service* classes
  (`Anthropic.Services.Beta.FileService` and ~14 siblings) taking an
  `HttpRequestMessage`, not an internal member of `ClientOptions`. Nothing on the
  options object accepts a header.
- **`OpenAI.GenericActionPipelinePolicy` is internal.** `AddPolicy` is public and takes a
  `PipelinePolicy`, but the concrete policy the report named cannot be constructed from
  outside the OpenAI assembly.

So neither proposed mechanism existed. What both SDKs *do* expose is a hook for a plain
`HttpClient`: `ClientOptions.HttpClient` on Anthropic, and
`ClientPipelineOptions.Transport` (via `HttpClientPipelineTransport`) on the OpenAI and
Azure families — `AzureOpenAIClientOptions` derives from `ClientPipelineOptions`
directly, so the Azure providers take the same path as OpenAI.

That makes it **one** mechanism rather than the report's two: `ProviderConfig`
(`nb.Providers.Abstractions`) returns an `HttpClient` wrapping a `DelegatingHandler`
that stamps the configured headers, and each provider hands it to its SDK's hook. It
returns `null` when the entry declares no headers, so an unconfigured entry keeps
whatever HTTP stack its SDK builds by default and nothing about existing behaviour
moves.

The handler *removes* a header before adding it, so a configured value replaces the
SDK's own of that name instead of appending a second value — the report's "or a
*different* header" case.

`ProviderConfig` lives in the Abstractions package rather than nb.Core precisely because
an out-of-tree provider should get the same three lines.

### The reported path is Anthropic *through* Cloudflare, and that is what is pinned

Worth being explicit, since "the Anthropic provider" and "talking to Anthropic" are not
the same route here. The reported configuration never reaches api.anthropic.com: it is
the `Anthropic` provider with `Endpoint` pointing at a Cloudflare AI Gateway, which then
forwards upstream. Both tests below run that shape — `Endpoint` set to the listener, not
omitted — so what is pinned is the gateway route, not direct Anthropic.

The part of that route most likely to break quietly is URL composition, so it is now
asserted rather than assumed. `Endpoint` is a *base*: the Anthropic SDK appends
`/v1/messages` to it, and the OpenAI-dialect SDKs append `/chat/completions`. A
Cloudflare `Endpoint` of

```
https://gateway.ai.cloudflare.com/v1/<account>/<gateway>/anthropic
```

therefore resolves to `…/anthropic/v1/messages`, which is the shape Cloudflare
documents. Supplying our own `HttpClient` does not disturb that — the SDK still builds
the URI from `BaseUrl` — and the tests assert the received path to keep it that way.

**Not verified against a live gateway.** The listener is a stand-in. What is proven is
that the header, the SDK's own auth header and the expected path all leave nb together;
whether Cloudflare accepts a given token is between the operator and Cloudflare.

### `ApiKey` when the gateway holds the key

Resolved as the report's first option: `RequiredConfigKeys` is unchanged, but
`ProviderConfig.HasRequired` excuses `ApiKey` when the entry carries headers, so the
stored-keys mode is expressible with no key at all and no dummy value. Several SDKs
still refuse to construct without a non-empty credential, so
`ProviderConfig.ApiKeyOrPlaceholder` supplies `"gateway"` — following the precedent
`LocalLlmProvider` already set with `"local"`.

**Not supported: Gemini.** The Mscc SDK accepts neither a custom base URL (already noted
in `GeminiProvider`) nor a custom HTTP stack, so there is nothing to hand a header to. A
gateway cannot front it at all, with or without this fix.

### Tests

`nb.Tests/ProviderHeaderTests.cs`, five, **all confirmed failing before the fix** —
three on a missing header, one on `CanCreate` rejecting a keyless entry, one on the
`${VAR}` expansion. They are the report's own test design: a loopback `HttpListener`
stands in for the gateway (so the Anthropic case is Anthropic-via-gateway, matching what
was reported, rather than a direct call), the entry sets `MaxRetries: 0`, and the listener answers 400
so the run gives up promptly — the assertion is on the request that went out, not on
anything coming back. Both SDK families are covered end to end, loading the real plugin
DLLs out of `bin/…/providers`, because they use different assemblies and only an
end-to-end check would catch one of them silently doing nothing.
