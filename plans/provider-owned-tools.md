---
kind: plan
title: Provider-owned tool surfaces — make new models pluggable
created: 2026-08-11
updated: 2026-08-11
status: current
state: draft
touches:
  files:
    - ConversationManager.cs
    - Facade/NbRuntime.cs
    - Program.cs
    - Providers/nb.Providers.Abstractions/IChatClientProvider.cs
    - Shell/EditFileTool.cs
    - Shell/ReadFileTool.cs
    - Shell/WriteFileTool.cs
  features: [tool-surface, provider-config, run-specs]
provenance:
  author: claude
---

# Provider-owned tool surfaces — make new models pluggable

## Context

Qwen3-Coder ("qwen coder next") behaves badly at file edits under nb. Root cause is that nb
advertises a tool surface that differs from the one the model was RL-trained against — the
`qwen-code` harness. Confirmed from `QwenLM/qwen-code` source
(`packages/core/src/tools/tool-names.ts` plus each tool's schema literal):

| nb | qwen-code (trained) |
|---|---|
| `edit_file(path, old_string, new_string, replace_all)` | `edit(file_path, old_string, new_string, replace_all)` |
| `write_file(path, content)` | `write_file(file_path, content)` |
| `read_file(path, offset, limit)` | `read_file(file_path, offset, limit)` |
| `find_files(pattern, path, max_results)` | `glob(pattern, path)` |
| `grep(pattern, path, file_pattern, case_insensitive, max_results, output_mode)` | `grep_search(pattern, path, glob, limit)` |
| `list_dir(path)` | `list_directory(path, ignore, file_filtering_options)` |
| `bash(description, command, timeout_seconds)` | `run_shell_command(command, description, directory, is_background, timeout)` |

The sharpest mismatch sits where the symptom is: `path` vs `file_path` on the file tools. A
model emitting `file_path` into nb's schema produces a call nb reads as an empty path — which
presents as the model "being dumb."

Qwen is not a special case; it is the case that made the general problem visible. GPT-family
models already needed their own edit surface, handled by `EditToolStyle`
(`nb.Core/Facade/NbRuntime.cs:68-76`) swapping in `apply_patch`. Each new model will want its
own names, schemas, and edit idiom. **The tool surface is a property of the model, not of nb.**

The generalization: a provider plugin owns the tool surface it advertises. nb.Core keeps the
executors and the safety story. New models become pluggable — you add a provider, you don't
patch the engine.

## Decisions

- **Tool surfaces are per-provider and native.** No central dialect registry, no canonical wire
  vocabulary, no translation the model can observe. What the model sees is what the provider
  declares.
- **Programs become provider-coupled.** Cross-provider testing now needs a program per
  provider, kept in sync by hand. This is accepted: the diagnostic has to be honest before it
  is convenient. A program that lies about the surface is worse than a program that only
  describes one.
- **Transcripts record wire names as sent.**
- **No provider-baked system prompts.** The `system.{provider}.md` resolution was already
  deleted in `6dec8be` ("De-soup the CLI") and only lives on as stale documentation at
  `CLAUDE.md:248`, which should be corrected. The files under `prompts/` stay in the repo as
  reference material for library consumers, loaded only by an explicit `system @file` directive.
  A default prompt arriving from config the program never names is the implicitness the
  conversation-program pivot removed on purpose.
- **This lands on a branch.** It is a breaking change to program portability and to the
  provider interface.
- **Out-of-tree providers get a transitional default, not a permanent one.** See below.

Precedent: MCP tool names already reach the model untranslated, one surface per connected
server. Provider-owned native tools make nb consistent with how it already treats MCP.

## Architecture

**Providers own data. nb.Core owns execution.**

A provider declares descriptors — name, description, JSON schema, and which nb capability each
tool binds to. It ships no executor code. If plugins owned execution, `TrustSandbox`,
`FileReadTracker`, read-before-edit, and the approval UX would fragment across
separately-versioned DLLs, and every provider author would reimplement the safety story.
Only data crosses the ALC boundary — strictly easier than the `IChatClient` that already
crosses it (`ProviderManager.cs:35` excludes the abstractions assembly from per-provider
scanning; M.E.AI types resolve through default-ALC fallback).

Each descriptor binds a wire tool to an nb capability and maps its wire parameter names onto
the executor's arguments. That mapping is the same data the rejected central dialect table
held — relocated to where it is true. No registry claims to know every model; each provider
declares its own surface, and a model nb has never heard of needs no engine change.

### `nb.Providers.Abstractions`

Version bumps to 1.1.0. Add a simple data type (record, primitives only) carrying: wire tool name, description, JSON
schema string, the bound capability id, and the wire-param → executor-arg map. Capability ids
are string constants — `ReadFile`, `WriteFile`, `EditFile`, `ApplyPatch`, `Bash`, `FindFiles`,
`Grep`, `ListDir`, `FetchUrl`, `Todo` — matching the existing `NativeToolNames` vocabulary at
`ConversationManager.cs:98-102`.

`IChatClientProvider` gains one member returning that set, **with a default interface
implementation returning nb's current surface**, marked deprecated on arrival per the
compatibility section below. That default is the "generic OpenAI" fallback, and it means every
existing provider — in-tree and out — compiles and behaves identically on day one.
A model graduates out of the generic surface by overriding the member — which is exactly the
`LocalLlm` story already in place, where an entry's `Provider` field can name an
implementation distinct from its label.

### `nb.Core`

- **Registration** (`ConversationManager.cs:344-397`): build `AIFunction`s from the provider's
  descriptors rather than from each tool class's `CreateTool()`. Today the schema is reflected
  off C# lambda parameter names, so the lambda names *are* the wire names; descriptor-driven
  registration replaces that with an explicit schema.
- **Dispatch** (`ConversationManager.cs:519-800`): this is the real work and the biggest risk
  in the plan. The current chain is ~11 arms of `functionCall.Name == "edit_file"` each reading
  arguments by hardcoded literal key (`"path"`, `"old_string"`, `"input"`, …). It becomes:
  wire name → descriptor → capability → executor, with arguments bound through the descriptor's
  param map. Approval routing is structural (which arm a call lands in), not table-driven, so
  it follows the capability and does not fragment.
- **`GetAvailableTools()`** (`:2143-2189`) is a hand-maintained mirror of the assembly block and
  must be driven from the same descriptors, or it will disagree with what was advertised.
- **`TranscriptPorcelainWriter.cs:25`** keys `PrimaryArgKeys` on argument names
  (`"command", "path", "pattern", "url", …`). With per-provider param names this set has to
  come from the descriptors, or porcelain silently degrades to compact JSON.

### The `tools` directive

Split by shape, which keeps most cross-provider programs portable without pretending:

- **Policy-shaped filters stay portable** — `tools none`, and capability-class filters such as
  "no mutation". These name policy, not wire tools, and mean the same thing everywhere.
- **Name-shaped filters are provider-specific** — `tools -edit_file` names a wire tool. Under a
  provider that doesn't advertise it, this is a hard error, not a silent no-op.

Most real uses are the first kind, so this absorbs much of the sync cost without softening the
honesty. It does not eliminate it, and is not meant to.

## Compatibility with out-of-tree providers

`nb.Providers.Abstractions` is a published NuGet package (`PackageId`, `Version 1.0.0`, MIT)
with at least one known external implementer maintaining a provider for an internal AI stack.
It is a real versioned contract, not an internal seam.

Adding the tool-surface member as a **default interface implementation** is source *and* binary
compatible: a provider compiled against 1.0.0 keeps compiling and keeps running against a newer
nb.exe, because the runtime resolves the new member to the default. The abstractions assembly
loads from the default ALC (`ProviderManager.cs:35`), so there is no version-skew trap. This is
a **minor bump to 1.1.0**. Doing nothing must never mean an external provider breaks.

The default is **transitional, with a one-major-version life**: documented as deprecated on
arrival, required at 2.0.0. External implementers get a working interim and a named deadline;
nb keeps the freedom to change its own canonical tool names later rather than freezing today's
names as a permanent compatibility surface.

Two things would escalate this from a minor bump to a real break and are therefore constrained:
making the surface member required rather than defaulted (deferred to 2.0.0), and collapsing
`EditToolStyle` into descriptors, which is config-visible (see Open).

Deliverables:

- **`CHANGELOG.md`** — does not exist today; the repo has `v0-stable` / `v0.9-beta` tags and no
  notes. Add it with a 1.1.0 entry stating what the interface gained, that it is defaulted, that
  no action is required now, and that it becomes required at 2.0.0.
- **A heads-up before the branch lands**, not a warning after. Send the external implementer this
  plan ahead of time. Not a code deliverable; tracked here so it doesn't get lost.

## Staging

Land in this order; each step is separately verifiable.

1. **`--resolve` tool enumeration.** Extend the existing flag (`Program.cs:100`, `:221` — "Print
   the effective envelope at each run point, run nothing") to print the exact tool surface the
   configured provider would advertise: names, schemas, parameters. No restructuring. This is
   the instrument for everything downstream and it is useful immediately.
2. **Measure qwen.** Run an edit-heavy program against qcoder with `--output jsonl` and read
   what the model actually emits. Two things to rule in or out: whether args arrive as
   `file_path`, and whether tool calls arrive well-formed at all — Qwen3-Coder emits XML tool
   calls and has a known serving-side failure where it drops the opening `<tool_call>` tag
   under the stock chat template ([Qwen3-Coder#475](https://github.com/QwenLM/Qwen3-Coder/issues/475)),
   which is indistinguishable from this bug and is a template problem, not an nb one.
3. **Descriptors + interface default.** Add the type and the defaulted interface member. All
   existing providers untouched; behavior byte-identical. Verifiable by diffing step 1's output
   before and after.
4. **Descriptor-driven registration and dispatch.** The invasive change. Still behavior-identical
   under the default surface, which is what makes it testable.
5. **A qwen surface.** Declare it on a provider and measure against step 2's baseline.

## Verification

- **Byte-identical default surface**: `--resolve` output before and after steps 3–4 must match
  exactly for every existing provider. This is the main safety net for the dispatch rewrite.
- **Wire-level, no model needed**: put a non-default surface on the Mock provider and run
  `echo 'run MOCK:…' | ./nb - --output jsonl` from `bin/Debug/net10.0`. Assert the advertised
  schema carries the declared names, that a call using them dispatches and actually edits the
  file, and that the transcript records the wire name. `Providers/Mock/MockProvider.cs:139`
  synthesizes args by tool name and needs updating.
- **Regression**: `dotnet test`. `ToolSurfaceTests`, `TranscriptSerializerTests`,
  `TranscriptMapperTests`, `TranscriptPorcelainWriterTests` hardcode today's names and must stay
  green under the default surface.
- **Live**: `ssh imp '~/.local/bin/swap-model qcoder'`, then the same program under the generic
  surface vs the qwen surface, comparing edit success rates.

## Open

- **Considered and deferred, not forgotten**: a `docs/provider-api.md` documenting
  `IChatClientProvider` as a public contract (it is nb's third written contract after the two
  conversation-program docs, and is currently undocumented outside the package README), and a CI
  test building a reference provider against only the published abstractions surface so boundary
  breaks fail the build. Both are worth doing; neither is committed scope here.
- Whether `apply_patch`/`EditToolStyle` collapses into this (a provider declaring the patch
  surface) or stays a separate per-entry knob. Collapsing is cleaner; it also means touching
  the one provider-specific mechanism that currently works.
- Whether descriptors are authored in C# or as a manifest file next to the provider DLL. C# is
  simpler and type-checked; a manifest would let a surface be tweaked without a rebuild, which
  matters when chasing a moving target like qwen-code's renames.
