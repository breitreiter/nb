# Can't configure two endpoints for one provider implementation

Status: Open (2026-07-26) — no workaround beyond editing `appsettings.json` in place.

---

## Symptom

`ChatProviders[]` looks like a list of *configurations*, so it reads as though
you can define several entries backed by the same provider implementation and
switch between them with `/provider <name>`:

```jsonc
{ "Name": "LocalCoder", "Endpoint": "http://127.0.0.1:8081/v1", "Model": "qwen3-coder-next" },
{ "Name": "LocalAir",   "Endpoint": "http://127.0.0.1:8082/v1", "Model": "glm-4.5-air" }
```

Neither entry works. Both fail at selection time with:

```
No provider found for: LocalCoder
Available providers: Mock, Gemini, LocalLlm, AzureFoundry, AzureOpenAI, Anthropic, OpenAI
Failed to initialize chat client. Please check your configuration.
```

(That list is in `providers/` directory-enumeration order, not sorted, which
doesn't help when you're scanning it for the name you typed.)

The config is well-formed and the endpoint is reachable; there is nothing wrong
with the entry that the error points at. The message names the entry you asked
for, not the field that actually failed, so the natural read is "the provider
DLL didn't load" — which sends you off inspecting `providers/` for a while.

## Cause

`ChatProviders[].Name` is doing double duty: it is both the config entry's key
*and* the lookup key for a loaded `IChatClientProvider`. Selection resolves the
implementation first and the config second, against the same string:

- `Providers/ProviderManager.cs:78` — `_providers.FirstOrDefault(p => p.Name == activeProviderName)`
- `Providers/ProviderManager.cs:89` — `providerConfigs.FirstOrDefault(c => c["Name"] == activeProviderName)`

Since `IChatClientProvider.Name` is a hardcoded property on the implementation
(`LocalLlmProvider.Name => "LocalLlm"`), the set of usable `Name` values is
fixed by what's compiled into `providers/`. Any entry whose `Name` isn't in that
set is unreachable, and two entries sharing a `Name` are also broken —
`FirstOrDefault` silently takes the first and the second is dead config.

The same coupling runs through everything keyed on the active provider name:
`ResolveMaxContextTokens`, `ResolveProviderFloat`, `ResolveActiveModelSlug`
(`Program.cs:172-208`), and the per-provider system prompt lookup
(`prompts/system.{activeProviderName}.md`, `Program.cs:415`).

## Why it bites repeatedly

It only shows up when one implementation legitimately fronts several distinct
backends, and `LocalLlm` is exactly that case — it's a generic
OpenAI-compatible client, so every local llama.cpp / Ollama / vLLM server is the
same implementation at a different port and model alias. Running a coding model
and a reasoning model on two ports is the normal setup, not an edge case.

`AzureFoundry` and `OpenAI` have the same shape now that both accept an
`Endpoint` override (`bcaecd2`) — one implementation, many possible backends —
so this widens rather than staying local-only.

## Repro

1. Add two `ChatProviders` entries with distinct `Name`s, both otherwise valid
   `LocalLlm` configs.
2. Set `ActiveProvider` to either one.
3. `./nb "hi"` → `No provider found for: <name>`.

Also: give both entries `"Name": "LocalLlm"`, and the second is silently
ignored with no diagnostic at all.

## Fix candidates

1. **Decouple the two keys.** Add an optional `Provider` field naming the
   implementation, and let `Name` be a free-form label for the entry:

   ```jsonc
   { "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "…:8081/v1", "Model": "qwen3-coder-next" }
   ```

   Fall back to `Name` when `Provider` is absent, so every existing config keeps
   working. Touches the two lookups in `ProviderManager` plus the four
   name-keyed resolvers in `Program.cs`, which should then key off the entry
   label while implementation resolution keys off `Provider`.

   Note this makes the system-prompt lookup per-*entry* rather than
   per-implementation (`prompts/system.LocalCoder.md`), which is arguably the
   more useful granularity anyway — but it is a behaviour change for anyone
   relying on the current file name.

2. **Better diagnostics only** (cheap, doesn't fix the limitation). When
   `ActiveProvider` matches a `ChatProviders` entry but no loaded provider, say
   so explicitly — "entry 'LocalCoder' exists but names no known provider
   implementation; `Name` must be one of: …" — and warn on duplicate `Name`s
   instead of silently taking the first.

Worth doing 2 regardless of whether 1 lands; the misleading error is most of
the cost of this bug.
