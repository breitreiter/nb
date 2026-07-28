# Can't configure two endpoints for one provider implementation

Status: Fixed (2026-07-28) — both candidates landed.

## Fix

New `Providers/ProviderEntries.cs` separates the two keys. `Name` is now a
free-form label; an optional `Provider` field names the implementation, and
defaults to `Name` when absent — so every existing config keeps working
untouched.

`TryCreateChatClient` now resolves the **config entry first** and the
implementation second. That ordering is the actual fix: the entry is what the
user selects, and the implementation behind it is a detail of the entry rather
than the other way round.

One thing in the report was wrong. It predicted the fix would have to touch the
four name-keyed resolvers in `Program.cs` — `ResolveMaxContextTokens`,
`ResolveProviderFloat`, `ResolveActiveModelSlug`, and the `EditToolStyle`
lookup. They all already key off the *config entry* by `Name`, which is exactly
right once the keys decouple, so none of them changed. Only implementation
resolution needed the indirection.

The system-prompt lookup did change meaning, as predicted, but not as a break:
prompt files now resolve by entry label **then** implementation, first match
wins. So `LocalCoder` picks up `system.LocalLlm.md` and
`system.LocalLlm.<modelslug>.md` unless it has files of its own. Nobody relying
on the current filenames loses anything, and per-entry prompts are available to
anyone who wants that granularity.

Candidate 2's diagnostics landed too, since they were most of the cost:

- An entry naming an unloaded implementation now says which field failed —
  `Entry 'Broken' names provider implementation 'NoSuchImpl' (via "Provider"),
  which is not loaded` — rather than pointing at the entry name and reading like
  a DLL load failure.
- An entry whose `Name` matches no implementation and has no `Provider` field
  gets the actionable form: *add a `Provider` field naming one, or rename the
  entry.*
- Duplicate labels now warn instead of silently discarding the later entry.
- All listings are sorted, and `ShowProviderStatus` enumerates entries (showing
  `LocalCoder (via LocalLlm)`) plus a trailing line naming loaded-but-unused
  implementations — without it the listing can't tell you what `Provider` is
  allowed to say.

Coverage in `nb.Tests/ProviderEntriesTests.cs` (11 tests), including this
report's two-entry fixture verbatim, comment-key entries as they appear in real
`appsettings.json`, and the duplicate-label case. Verified end-to-end against
two local servers: `LocalCoder`/`LocalAir` on ports 8081/8082 both resolve, and
all four diagnostic paths were exercised against a running binary.

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

---

## Predates the conversation-program merge (2026-07-28)

Written against the pre-`nb.Core` architecture — interactive REPL, per-directory
conversation history, kits — which the merge in `92da725` replaced. Paths and
symbols named above may have moved: `Providers/ProviderEntries.cs` is now
`nb.Core/ProviderEntries.cs`, and `Providers/ProviderManager.cs` is
`nb.Core/ProviderManager.cs`.

The fix carried through the merge and its tests still pass. One thing noticed in
passing and deliberately not chased: `nb.Core/ProviderConfigResolver.cs`, which
arrived on the merged branch, hand-rolls three `provider["Name"]` lookups of its
own rather than going through `ProviderEntries`. Those are label lookups, so they
look correct as written — but they were not audited, and this report is the place
to settle whether they should share the seam. Do that when closing.
