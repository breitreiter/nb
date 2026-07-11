---
kind: rule
title: Per-model behavior lives in prompt layers, not engine control flow
created: 2026-06-03
updated: 2026-06-03
provenance:
  source: human
enforces:
  - ConversationManager.cs
  - McpManager.cs
  - Shell/**
  - "*Tool.cs"
---

# Per-model behavior lives in prompt layers, not engine control flow

The engine must be correct on any untuned `IChatClient`, and all
model- or provider-specific *behavior* must live in data — the layered
`system.<provider>.md` / `system.<provider>.<model>.md` prompt files —
never in engine control flow. We support capability **tiers**, not an
enumerated list of models.

## The three clauses

1. **Correct untuned.** With no model-specific prompt file present, the
   engine produces correct (if not optimal) behavior on any model that
   speaks `IChatClient`. A per-model file may *improve* behavior; the
   baseline must never be *wrong* without one. A model that is wrong
   untuned is a tier-3 model failing the bar — drop it from the
   supported set rather than bending the engine around it.

2. **Policy is data, not code.** Per-model and per-provider behavior is
   expressed only as prompt text in the layered `system.*.md` files
   (assembled in `Program.cs:381-394`). The engine — the turn loop, tool
   registration, the reminder logic, provider dispatch — contains **no
   conditional branch keyed on a model or provider identity**. Loading a
   policy file by model slug (`Program.cs:386-394`) is the *sanctioned*
   mechanism and is explicitly allowed; branching engine logic on the
   slug is not.

3. **Provider mechanics stay behind the plugin.** Provider-unique turn
   mechanics (e.g. OpenAI `phase` / `previous_response_id`) stay inside a
   plugin's `IChatClient`, or are not built. They do not enter the shared
   engine and `IChatClientProvider` is not widened to carry them. See the
   provider-boundary note distilled from the todo-tool work.

## Why

- The data plane (messages, tools, streaming) genuinely converged into
  `IChatClient` and still holds. What diverges across models is the
  *policy* plane (how to prompt, when to plan, how hard to push
  completion) — which was never converged; pre-GPT-5 it only looked
  unified because the field was a two-model monoculture.
- Tiering bounds maintenance to ~constant (number of capability tiers),
  not unbounded (number of models). The long tail gets the default tier
  and "good enough."
- nb is the proving ground for downstream projects. The artifact that
  fans out is this discipline — `IChatClient` core, markdown policy, no
  model branches in code — not any individual model's prompt. Inherit the
  discipline and the downstream projects are inoculated; inherit
  per-model handling and they rot in lockstep.

## How violations look

- A model- or provider-name string comparison in the engine, e.g.
  `if (model.Contains("qwen"))` or `if (providerName == "AzureFoundry")`
  inside `ConversationManager`'s turn/tool/reminder path. (The canary:
  the engine currently has **zero** such branches. Keep it zero.)
- A provider plugin that requires the engine to special-case its
  request/response handling instead of absorbing it behind `IChatClient`.
- A new field on `IChatClientProvider` whose purpose is to carry
  provider-specific turn metadata into shared code.
- Behavioral steering (plan-first nudges, completion pressure) hard-coded
  into a globally-registered tool description instead of the prompt layer,
  when that steering helps some tiers and hurts others.
