---
kind: plan
title: search_web — capturing search intent as an observable
created: 2026-08-12
updated: 2026-08-12
status: current
state: draft
touches:
  files:
    - ConversationManager.cs
    - Facade/NbRuntime.cs
    - Shell/FetchUrlTool.cs
    - MCP/FakeToolManager.cs
  features: [tool-surface, transcript, diagnostics]
provenance:
  author: claude
---

# search_web — capturing search intent as an observable

## Context

nb currently offers `fetch_url` (`nb.Core/Shell/FetchUrlTool.cs`) but no search tool. A model
under nb that wants to consult the internet has no way to say so. It either guesses a URL and
calls `fetch_url`, shells out via `bash` (curl), or — most often — silently doesn't, and
answers from parametric memory instead.

That last case is the problem. **The absence of a search tool doesn't produce a null reading;
it produces a distorted one.** A model that would have searched, and can't, does something
else instead, and nothing in the transcript marks the substitution. Someone evaluating that
transcript sees a confident unsourced answer and cannot tell whether the model believed it or
was cornered into it.

nb is a diagnostic instrument, not an agent harness. The goal here is not to give models good
search. It is to make "the model wanted to search the internet at this point" a **recorded,
inspectable event** in the transcript, so evaluators can factor it in.

That framing decides most of the design, and it decides it differently than a harness would.

## Decisions

### 1. nb-executed, never provider-hosted

Anthropic and OpenAI both offer server-side search: you declare a tool by type, the provider
runs the query inside the same request, and results arrive as content blocks. It is cheaper to
implement than anything below — and it is **disqualified for nb's purpose**.

Server-side execution never produces a client-visible tool call. There is no `tool_use` block
to satisfy, no round trip, nothing for `TranscriptMapper` to turn into a `ToolCallEvent`. The
provider hides exactly the signal we are trying to record. A diagnostic tool cannot delegate
observation of the thing it observes.

Secondary reasons, all sufficient on their own:

- It is Anthropic/OpenAI-only. Local models via `LocalLlm` get nothing, and those are a large
  part of what nb is used to study.
- `IChatClient` doesn't model provider server-tool blocks, so results would surface as opaque
  content `ConversationManager` can't render or account for.
- Provider-side search introduces `pause_turn` resumption semantics into the turn loop for a
  feature we don't otherwise need.

So: `search_web` is a native tool in `nb.Core/Shell/`, executed by nb, recorded like any other.

### 2. The name is `search_web`

Not `web_search`. Two reasons, in priority order:

1. **It matches nb's existing convention** — `find_files`, `fetch_url`, `read_file`,
   `list_dir`. Verb first. `web_search` would be the odd one out.
2. **`web_search` is a reserved name on at least one provider.** Anthropic's built-in
   client-side tools (`bash_20250124`, `text_editor_20250728`, `memory_20250818`) are
   schema-less: the input schema lives in the model's weights, bound to the name. Anthropic's
   docs explicitly warn that defining a custom tool named `bash` yields a user-defined tool
   *without* the built-in behavior. `web_search` sits in that same family. Shadowing a trained
   name means the model may bring schema expectations we don't satisfy — the qwen-code failure
   mode from `tool-dialects.md`, self-inflicted.

If a future dialect wants to present this as `web_search`, that's a mapping. The default
should not collide.

### 3. Two modes: declared-only and live. No fake search.

A diagnostic instrument that returns different results on every run is a poor instrument. Live
search makes a program non-reproducible: same program, same seed, different transcript,
because the internet moved. That defeats the comparison workflows nb exists for.

So `search_web` ships with **no live backend wired by default**. Declaring the tool is what
captures intent; executing it faithfully is a separate, opt-in concern.

Two execution modes, and no third:

| Mode | Behavior | Use |
|---|---|---|
| **Declared-only** (default) | Tool is advertised; a call returns "no search backend configured" | Pure intent capture. Deterministic. |
| **Live** | A configured backend queries | Fidelity, at the cost of reproducibility |

**Fake search is deferred, and one of its two use cases is already shipped.** The tempting
third mode splits into two variants, and neither earns a feature:

*Pin the results and study what follows.* This needs no search feature at all — it is a
**fabricated tool round**, which programs already support (`docs/conversation-program-cli.md`
§ tool round as premise; JSONL in the program file, or `.Add(events)` via the builder). Write
the assistant turn that called `search_web` and the result it "got", then continue. The
fabrication lives in the program source where the author put it deliberately and a reader can
see it — which is the right place for a lie. Available today, before any of this lands.

*Answer arbitrary live queries with static results.* This is the variant that would need
building, and it doesn't work. Models are good enough to notice that every query returns the
same ten links, and they say so — at which point the run is measuring how the model reacts to
a broken tool, not how it searches. A study of our own stub.

Record/replay against a real backend would give determinism with real provenance, and is the
only version of this worth revisiting. It is not needed for intent capture, so it waits until
something concrete demands it.

`FakeToolManager` (`nb.Core/MCP/FakeToolManager.cs`) still works on `search_web` like any other
tool; it just isn't a path the docs should recommend, for the same reason — a hand-authored
fixture of invented sources is fabricated web content filed as evidence.

### 4. One live backend, badly

When live mode lands, it gets exactly one backend behind a `SearchProvider` config key, and it
returns title + URL + snippet. No reranking, no domain filters, no content extraction, no
result-count tuning, no caching layer. If a program needs page content, that's what `fetch_url`
is for, and the search→fetch sequence is itself an interesting thing to observe.

The temptation to build a good search feature should be resisted explicitly, because every
knob added is a behavior the tool description has to describe, and every line of description is
a variable in the experiment.

### 5. Approval and surface directives follow existing rules

- Approval matches `fetch_url` — network egress, prompts per call, pre-approvable via
  `--approve` for scripted runs. Trust mode does not auto-approve it (it leaves the sandbox by
  definition).
- Registration in `NbRuntime` alongside the other native tools means
  `tools none +search_web` works with no new plumbing, since `ToolSurface.NativeAllow`
  (`nb.Core/Transcript/ToolSurface.cs`) is seeded from the native tool list.
- No new transcript event type. A search is a `ToolCallEvent` with `name: search_web`. Grepping
  jsonl for search intent is then the same operation as grepping for any other tool.

## Fabricated results are the dangerous failure

The failure mode that actually threatens nb's output is not under-triggering. It is a model
that writes plausible search results into its own prose — titles, URLs, snippets, all invented.
Earlier OpenAI models did this readily, and a tool that is declared but returns nothing is
plausibly a *provocation* toward it: the model has been told the capability exists, been
refused, and can close the gap by imagining what the results would have said.

This is worse than under-triggering because it is confident and legible. A transcript
containing fabricated sources looks more authoritative than one containing none.

**The schema already keeps the provenance straight, and that is not an accident worth
losing.** Fabricated results appear as `AssistantTextEvent`; only nb's own tool execution
produces `ToolResultEvent`. nb therefore never records invented content as tool output — the
distinction survives in the jsonl even when it's invisible to someone skimming rendered
output. Two consequences:

- **Never blur that line.** No "synthesized" or "simulated" results injected as tool results,
  by nb or by a fixture. The reason hand-authored fakes are rejected above is this same rule.
- **`--seed` is the contamination path to watch.** Seeding a transcript that contains
  fabricated results replays them as premise into the next run, where they are now
  indistinguishable from user-supplied context. Worth a look when this lands, though the fix
  may belong to seeding rather than here.

Detection is mostly a reading problem, not a code problem, but it is cheap to help: porcelain
output could make the assistant/tool boundary visually unmissable around anything
search-shaped. Not blocking, and not something to over-engineer — the structural guarantee is
the real defense.

Worth measuring once this exists: whether declared-only mode induces fabrication at a higher
rate than having no search tool at all. If it does, that's a genuine argument for gating the
tool off by default rather than declaring it — and it is exactly the kind of question nb is
supposed to be able to answer about itself.

## The honest tension

Declared-only mode captures intent perfectly and models the *consequences* of search wrongly.
A model told "no search backend configured" proceeds differently than one handed ten results —
possibly quite differently, since it now knows a capability was dangled and withdrawn.

There is no mode that gives both perfect determinism and perfect behavioral fidelity; that is
a property of the problem, not of the design. What matters is being able to *pick which one
you're measuring*, and to say so in the program. Declared-only answers "would it search?".
Live answers "what actually happens end to end?" and gives up reproducibility to do it. The
third question — "given these results, what then?" — is answered deterministically by a
fabricated tool round, which is a program-authoring move rather than a mode of this tool.

Worth documenting in `docs/conversation-program-cli.md` when this lands, because a reader who
assumes declared-only is neutral will misread their own transcripts.

## Evidence on tool naming

Whether a third-party search tool gets called at the rate a native one would is a real
question, and the public evidence is thinner than it should be. No published head-to-head of
Claude/GPT with native vs. third-party search on a shared task set could be found.

What does exist:

- **[ToolTweak](https://arxiv.org/html/2510.02554v1)** finds tool *names* strongly influence
  selection — not just descriptions, which is where prior work had focused. Two functionally
  identical tools differing only by a numeric suffix drew drastically different selection
  rates. Tested on DeepSeek Chat, Gemini 2.5 Flash Lite, GPT-OSS-20B, Grok 3 Mini, Llama
  3.1-8B, Qwen 2.5-7B — no Claude or frontier GPT, so it establishes the mechanism, not the
  vendor-specific claim.
- **[The Interplay of Harness Design and Post-Training in LLM
  Agents](https://arxiv.org/abs/2606.25447)** frames the `tool-dialects.md` thesis directly:
  the harness — which tools are exposed, how they're described, what accompanies each
  observation — is treated as fixed engineering detail while the model is post-trained around
  it. **Results not yet read**; the PDF didn't extract cleanly. Worth a proper pass, as it is
  the closest thing to a citation for the dialects work generally.
- Anthropic's own migration guidance notes that on recent models, tool triggering is
  "surface-dependent," and recommends prescriptive "call this when…" trigger conditions in the
  tool description to recover call rate.

Practical consequence: **write the description prescriptively**, stating when to call, not just
what it does. Under-triggering is the failure mode to watch, and it is quiet — a search that
doesn't happen leaves no trace, which is the exact blind spot this plan exists to close. If
under-triggering shows up in practice, it is a dialects problem (name + description per
provider), not a reason to redesign this.

## Non-goals

- Search quality, result ranking, or provider comparison.
- Provider-hosted search, in any mode. See decision 1.
- A general "web" capability. `fetch_url` exists; this is orthogonal.
- Making models happy. If a model dislikes nb's search surface, that dislike is a finding.

## Open questions

- What exact wording does declared-only return? **Settled: explicit** ("no search backend
  configured"), not a neutral "no results" — nb does not lie inside a document people use as
  evidence, and a model that knows the tool is unwired is less likely to invent what it would
  have returned. Remaining question is only phrasing: it should read as an unconfigured tool,
  not as a failed search.
- Does `search_web` need its own `--approve` pattern shape, or does the existing string
  matching in `ApprovalPatterns` cover a query argument well enough?
- Is fake-by-default surprising enough to warrant a startup warning when the tool is enabled
  with no backend? Probably yes at first use, not every call.
