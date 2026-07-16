---
kind: plan
title: Portable model selection — fixed connections, open models
created: 2026-07-15
updated: 2026-07-15
status: current
state: proposed
touches:
  files:
    - Program.cs
    - ProgramEvaluator.cs
    - Providers/ProviderManager.cs
    - appsettings.example.json
  features: [conversation-program, providers, headless, library-facade]
provenance:
  author: claude
---

# Portable model selection — fixed connections, open models

## Why this note exists

The conversation-program thesis (`plans/conversation-program-evaluator.md`) is
that **one document is one runnable program** — provider, model, tool surface,
approval policy, fabricated history, and the live prompt, all in a single
ordered stream. It very nearly holds. The one place it leaks is the provider
*connection*: `provider <name>` is a bare selector that resolves against a
`ChatProviders` block already present in the host's `appsettings.json`
(`ProviderManager.TryCreateChatClient`, matched by `Name`). The program can name
a provider; it cannot describe how to reach one.

The motivating consumer wants to **supply arbitrary models and own their keys.**
There are two ways to serve that, and they differ entirely in security posture:

1. **Ship the connection** — let a program carry endpoint + key + model inline,
   so it is fully self-contained. Maximally portable, but it puts a secret into
   a shareable, round-tripping artifact. Sketched at the end as the deferred
   alternative; it is heavier precisely because of the secret handling it forces.
2. **Fix the connection, open the model** — keep endpoint + key in
   `appsettings.json` (per-machine, never travels) and let the program carry
   only the **model name**, which is not a secret.

**This note designs (2).** It is the better trade for the motivating consumer:
it delivers arbitrary model selection with *zero* key-shipping surface, and — as
it turns out — it is almost entirely already built. Azure and OpenRouter are the
exact shape it serves: one endpoint + one key, many models chosen per request.

## The principle: the key is the secret; the model name is not

Everything follows from one asymmetry. An API key and an endpoint are
credentials — they belong to a machine, must not land in a committed program, a
saved transcript, or emitted bytecode. A **model id is a plain string**: safe to
commit, share, round-trip, and paste into a transcript. So the clean seam is:

- **Connection (endpoint + key) is config.** It lives in `ChatProviders`,
  resolved by the host, never serialized into a program.
- **Model is program.** The `model <name>` directive carries it, and it is
  portable by construction because it holds no secret.

This keeps `plans/composable-cli-reorientation.md` Pillar 2 (connection config
lives in the config layers) fully intact — nothing about *reaching* a provider
moves into the program. Only the *choice of model on an already-reachable
provider* becomes program-carried, and that choice was never a secret.

## What already works

`model <name>` in a program flows through the evaluator (`ModelEvent` → `Model =
name` → `SwapClient()`) to the client factory (`Program.cs:BuildProgramClient`),
which calls `OverrideProviderModel`. That writes the model onto the **active
provider's existing config block in memory** — keys untouched, nothing shipped:

```
model llama-3.3-70b-instruct     # against whatever provider appsettings defines
run summarize the log
```

The override is per-run and in-memory only: a program may switch models between
runs (`model A` / run / `model B` / run) and each `SwapClient()` re-applies it.
No file is mutated, nothing persists, no credential is read or emitted.

Surveying what each installed provider reads for its model, the override lands
correctly for nearly all of them:

| Provider | Model field it reads | `model` directive |
| --- | --- | --- |
| OpenAI-compatible (**OpenRouter**) | `config["Model"]` | works |
| Anthropic | `config["Model"]` | works |
| Gemini | `config["Model"]` | works |
| **AzureFoundry** (modern Azure) | `config["Model"]` | works |
| LocalLlm | `config["Model"]` | works |
| **AzureOpenAI** (classic, deployment-based) | `config["ChatDeploymentName"]` | **was silently ignored — fixed below** |

## The one gap, and its fix (done)

`OverrideProviderModel` hardcoded the field name `Model`, but classic
`AzureOpenAIProvider` reads `ChatDeploymentName`
(`Providers/AzureOpenAI/AzureOpenAIProvider.cs`). So a `model` directive against
a classic AzureOpenAI block did nothing — no error, just the configured
deployment. Modern Azure (Foundry, which reads `Model`) was already fine; only
the deployment-based path was affected.

**Fix:** `OverrideProviderModel` now writes **both** keys —
`ChatProviders:{i}:Model` *and* `ChatProviders:{i}:ChatDeploymentName` — so the
override lands whichever field the target provider reads, with no provider-kind
knowledge in the override path. Three lines; the alternative (teaching
AzureOpenAI to fall back to `Model`) would touch a plugin for no extra benefit.
This makes `model` selection work uniformly across every installed provider,
classic Azure included. Landed in `Program.cs`.

With that in place, the compromise is *complete* for the read path: a portable
program can select any model on any configured provider, and no key ever leaves
`appsettings.json`.

## The optional operator guard: `AllowedModels`

The residual risk under this design is not secrets — it is that a portable
program can name **any** model on the operator's endpoint/key: any spend on an
OpenRouter account, or a model the operator didn't intend to expose. If the
operator wants a leash, add an optional per-block allowlist:

```json
{
  "Name": "OpenRouter",
  "Endpoint": "https://openrouter.ai/api/v1",
  "ApiKey": "${OPENROUTER_KEY}",
  "Model": "meta-llama/llama-3.3-70b-instruct",
  "AllowedModels": ["meta-llama/*", "anthropic/claude-*"]
}
```

Semantics:

- **Absent → any model allowed** (today's behavior; the common case stays
  frictionless).
- **Present → a `model` directive must match one entry** (glob, matched the way
  `Approval.McpTools` globs already match). A non-matching `model` is a
  hard-fail at `SwapClient()` time — the same class of failure as a bad provider
  name — with a message listing the allowed patterns.
- It is the operator retaining control of the one thing the program can now vary.
  Connection stays fixed in config, model becomes program-selectable, and
  `AllowedModels` bounds *which* models — three layers, each owned by the right
  party.

Wiring: the check belongs at the factory / evaluator boundary
(`BuildProgramClient` reads the active block's `AllowedModels`; `SwapClient()`
surfaces the failure through the existing warning/exit path), not inside a
driver — it is nb policy over a config surface, not an API-dialect concern.

## Worked examples

**OpenRouter — one key, many models, one portable program:**

```
model anthropic/claude-sonnet-5
system you are a terse assistant
run summarize the attached log
```

The host's `ChatProviders` has a single OpenRouter block (endpoint + `${…}`
key); the program names a model and nothing else. Ship the program anywhere with
OpenRouter configured and it runs — no key in the file.

**Azure — deployment selected per program, now that the field-name gap is
fixed:**

```
model gpt-5.3-codex
run refactor this function
```

Against a classic AzureOpenAI block this now overrides `ChatDeploymentName`;
against a Foundry block it overrides `Model`. Same directive, both Azure paths.

**Mid-stream model switch (cheap then careful), still one document:**

```
model meta-llama/llama-3.3-70b-instruct
run draft a first pass
model anthropic/claude-sonnet-5
run now critique and tighten the draft
```

## `--resolve` and `--validate`

- **`--resolve`** prints the effective provider and model at each `run` point.
  Under this design there is nothing to redact — the model is not a secret and
  the connection was never in the program — so `--resolve` is a clean, complete
  view of what each run will hit.
- **`--validate`** gains one semantic check when `AllowedModels` is present on
  the active block: every `model` directive resolved against that block must
  match the allowlist, reported the same way an unknown provider name is.

## Three surfaces, one program

- **CLI / source** — the `model` directive, as above.
- **Library** — `Nb.Program().Model("anthropic/claude-sonnet-5")` — a method
  call, already the builder's shape; no new surface.
- **REPL** — `/model anthropic/claude-sonnet-5` reconfigures the live session,
  effective at the next `run` (enter), exactly as today.

All three emit the same `ModelEvent`. This design adds **no new bytecode** — it
makes the existing `model` event work uniformly (the Azure fix) and adds one
optional config-layer policy (`AllowedModels`). That minimalism is the point: the
compromise is mostly the removal of an accidental limitation, not a new feature.

## The boundary: still construction, not composition

Selecting a model is **configuration** — it asserts state and consumes no prior
turn's output. It sits squarely inside the anti-composition boundary
(`plans/conversation-program-evaluator.md`, "The boundary"). `AllowedModels` is
host policy checked against that state, not dataflow between directives. Nothing
here crosses the line.

## Alternative considered (deferred): inline connection

The maximal form lets a program carry the whole connection inline, so it needs
*nothing* pre-registered:

```
provider mylab via=OpenAI \
  endpoint=https://api.mylab.internal/v1 \
  key=${MYLAB_API_KEY} \
  model=llama-3.3-70b-instruct
```

`<name>` is a local alias, `via=` names the installed **driver** (drivers are
code and stay installed; only the *connection* would be inlined), and the
`key=value` attributes synthesize an in-memory `IConfiguration` handed to the
existing `CreateClient` seam — no driver plugin changes. It would also fold the
model-override path in as the common single-field case and delete
`OverrideProviderModel` entirely.

**Why it is deferred, not chosen.** It puts a credential into a shareable,
round-tripping artifact, which forces a whole secret-handling regime the
compromise avoids outright:

- `${VAR}` references must expand at **client-build time, not parse time**, so
  the `ProviderEvent` and its emitted bytecode retain the *reference*
  (`${MYLAB_API_KEY}`), never the resolved secret.
- Literal inline secrets (an author who ignored `${VAR}`) must be **redacted on
  emit** — any `ApiKey`/`Token`/`Secret`/`Password`-shaped field written as a
  placeholder with a warning.
- `--resolve` must redact the same set.

That is real surface area and a standing footgun, taken on only to remove a
pre-registration step. The motivating consumer's actual need — arbitrary models,
own the keys — is fully met by the compromise (their key is *already* in their
`appsettings.json`; only the model needs to travel). Inline connection earns its
complexity only if a concrete consumer appears who must run against an endpoint
the host cannot be configured for ahead of time. Until then it is documented
here and left unbuilt. The synthesize-an-`IConfiguration` mechanism is the seam
to reuse if it is ever picked up.

## Open questions

- **`AllowedModels` match semantics.** Reuse the `Approval.McpTools` glob
  matcher (recommended, one implementation) vs. a distinct model-id matcher.
- **`AllowedModels` scope.** Per-block only (proposed), or also a top-level
  default that individual blocks may override.
- **`--validate` strictness.** Should a program that selects a model *outside*
  `AllowedModels` fail `--validate`, or only fail at run time? Proposed: both,
  so a bad program is caught before any run.

## Related documents

- `plans/conversation-program-evaluator.md` — the thesis whose last leak this
  closes; the boundary this honors; the `mcp` "connection stays in config"
  precedent this design *keeps* (unlike the deferred inline form).
- `plans/composable-cli-reorientation.md` — Pillar 2 (connection config lives in
  the config layers) holds fully here; only non-secret model choice moves into
  the program.
- `plans/transcript-schema.md` — the `model` event already exists; this design
  adds no new bytecode.
