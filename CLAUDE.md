# Project Context for Claude

## Project Overview
NotaBene (nb) - A C# tool that evaluates **conversation-programs**: ordered directive
documents (provider/model/harness/tool-surface/approval/fabricated-history/prompt) that
nb runs statelessly. Consumable as a CLI (`nb <program-file>` / stdin) or an in-process
library (`nb.Core` → `Nb.RunAsync`). Pluggable AI providers, MCP integration.

## Key Technologies
- **Language**: C# (.NET)
- **UI Framework**: Spectre.Console for terminal UI
- **AI Integration**: Microsoft.Extensions.AI with pluggable provider architecture
- **Architecture**: a stateless conversation-program evaluator (nb.Core engine + a thin CLI Exe), pluggable provider plugins, MCP integration

## Important Documentation Links
- [Microsoft.Extensions.AI Documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI .NET SDK](https://github.com/openai/openai-dotnet)
- [Azure OpenAI SDK Examples](https://github.com/openai/openai-dotnet/tree/main/examples)
- [Spectre.Console Documentation](https://spectreconsole.net/)
- [MCP Specification](https://modelcontextprotocol.io/)
- [.NET MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [PdfPig PDF Library](https://github.com/UglyToad/PdfPig) (Apache-2.0 — replaced iText7, which is AGPL and incompatible with nb's MIT license)

## Build & Test Commands
```bash
# Build the project
dotnet build

# Run from bin directory (providers only load from here)
cd bin/Debug/net10.0

# Run a program (quick test via stdin; MOCK: prompts drive the Mock provider)
echo 'run MOCK:response=hi' | ./nb - --output jsonl

# Tests. Always build first — see the provider gotcha below.
dotnet build && dotnet test --no-build

# Integration evals. CI runs these too, so a green `dotnet test` is NOT enough.
./evals/run.sh --skip-llm
```

Note: `dotnet run` from project root won't work - provider DLLs are discovered relative to the executable.

**⚠️ `dotnet test` alone can run against a stale provider DLL.** Providers (including
`Providers/Mock`, which nearly every test drives) load at runtime through
`AssemblyLoadContext`, so they are *not* in the test project's dependency graph and
`dotnet test` will not rebuild them. Edit a provider, run `dotnet test`, and the suite
silently exercises the previous build — the failure looks like a broken test rather than
a stale binary. Run `dotnet build` first whenever provider code changed.

**⚠️ `evals/run.sh` is part of CI and `dotnet test` does not cover it.**
`.github/workflows/test.yml` runs `dotnet build`, `dotnet test`, *then*
`./evals/run.sh --skip-llm`. The evals drive the built binary end to end and assert on
**model-visible strings** — refusal text, exit reasons, trailer fields — so any change to
what nb says to a model can pass the unit suite and still break CI. Run both before
calling a change done.

## Fixing a bug: write the failing test first — when it's worth it

For **bug fixes**, prefer writing the regression test before the fix, and *observe it
fail*. Not as ceremony: for a bug, red is doing real work. It proves you reproduced the
reported bug rather than something adjacent to it — a regression test that was green
before the fix is testing nothing, and you won't find out until the bug is re-reported.
This is already the repo's standard for a good fix; several reports state it as a claim
(`bugs/Bash_Escapes_Dollar_Inside_Single_Quotes.md`: *"nine confirmed failing against the
unfixed tool"*).

What matters is **observed red**, not the ordering. Writing the test after the fix and
then stashing the fix to watch it fail is equivalent, and is often better when the fix is
what taught you where the assertion goes.

**This is not a suicide pact.** Skip the test — or write it after, or not at all — when:

- The behaviour isn't changing (a bug resolved as *accepted by design*, or closed on
  *use case retired*). There is no red state to observe.
- Reproducing it honestly is disproportionate (a runaway 20 MB/s producer, an OOM, a
  real network partition). Test the new bounded behaviour instead, and say in the report
  that it is not a reproduction.
- It's a race. A genuinely red test for a concurrency bug is flaky by construction; a
  serialising workaround plus a comment beats a test that fails 3% of the time
  (`nb.Tests/ConsoleBoundCollection.cs`).
- **You'd likely have to rewrite the test as you learn the fix.** A test you rewrite to
  match what you built records a guess, not a fact, and it destroys the whole point of
  writing it first. If the assertion isn't yet obvious, build first and test after.

The rule of thumb: write the test first when it encodes an **observation** (this input
produces this wrong output). Don't when it encodes a **guess** about an interface you're
still designing. Bugs are usually the former, which is why this applies to fixes and not
to new features.

Best fits in this codebase: anything whose wrong behaviour is a **string a model or a
human reads** — `approval_reason` values, refusal text, exit reasons, trailer fields.
Cheap to assert, cheap to see red, and `evals/` already asserts on exactly those.

## Execution Modes
nb runs a **conversation-program**, two ways:

1. **File / stdin** — `nb <program-file>`, `nb -`, or piped stdin runs a program and
   exits. Stateless; continuity is explicit via `--seed`. Default output is jsonl.
   The positional argument is a file path, never a prompt.

2. **REPL** (`nb` on a TTY, no input) — a live interpreter of the *same source syntax*:
   each entered line is a program directive; `run` invokes. Ctrl-D exits. The
   authoring/debug surface.

Full grammar/semantics: `docs/conversation-program-cli.md`; library API:
`docs/conversation-program-api.md`.

## Project Structure

**Two assemblies** (split in Phase 6b): `nb.Core/` is a self-contained library holding
the whole engine (referenceable by a Roslyn-built consumer, no NuGet); `nb.csproj` is a
thin CLI `Exe` referencing it. Namespaces stay `nb.*` across both. A same-solution
consumer references `nb.Core` and calls `Nb.RunAsync` in-process.

CLI Exe (repo root):
- `Program.cs` - The CLI shell: parse flags, resolve program input (file/stdin/REPL), emit/exit. A thin shell over `nb.Core` (`Nb.RunAsync` for a run, `NbRuntime` for the REPL)
- `FileMentionSource.cs` - Line-editor `@file` completion source (UglyPrompt)

`nb.Core/` library:
- `Facade/` - In-process library surface: `Nb.RunAsync(config, program, options) → RunResult` (the "one contract, three surfaces" entry point), `NbProgramBuilder` (fluent program authoring), `NbRuntime` (the shared engine assembler — throws `NbStartupException` rather than exiting, suppresses chrome), `NbOptions` (incl. `ProvidersDirectory` for library hosts).
- `ConversationManager.cs` - LLM interactions via Microsoft.Extensions.AI, MCP tool integration
- `ProviderManager.cs` - AI provider discovery/loading (plugin architecture; injectable providers dir)
- `ProgramEvaluator.cs` - Evaluates a conversation-program (the TranscriptEvent stream)
- `Transcript/` - The wire schema (events, serializer, mapper, loader, program parser)
- `Harness/` - The advertised tool surface. `NbHarness` (nb's own surface + the tool-execution capabilities), `HarnessRegistry` (the closed set of costume names), and one subclass per costume (`QwenCodeHarness`, `CodexHarness`, `ClaudeCodeHarness`). A costume swaps names, schemas, result strings and prompt furniture; the tools behind it are the same instances. Preambles are deployed data files in `nb.Core/prompts/harness/*.md`, not embedded resources. Design: `plans/harness-emulation.md`
- `MCP/` - `McpManager` (client lifecycle, layered mcp.json), `FakeToolManager`
- `Shell/` - Native tool implementations (bash, file I/O, find_files, grep, trust path-scoping, bwrap)
- `Utilities/` - `ConfigurationService` (layered config), `UIColors`, markdown rendering

Other:
- `Providers/` - External AI provider plugin projects (Anthropic, AzureOpenAI, …) + `nb.Providers.Abstractions` (the `IChatClientProvider` interface)
- `providers/` (in `bin/`) - Deployed provider plugin DLLs, loaded at runtime via `AssemblyLoadContext`
- `mcp-servers/mcp-tester/` - Built-in MCP server for testing and example prompts

## Development Notes
- The REPL parses each entered line with `ProgramParser` and feeds it to a long-lived
  `ProgramEvaluator` via `EvaluateEventAsync` (same grammar as a source-syntax program)
- Configuration is loaded from `appsettings.json`
- MCP clients are initialized on startup and disposed on exit
- Tool calling safety: Configurable max tool calls per message via `MaxToolCalls` in appsettings.json (default: 25)
- **Stateless**: nb reads and writes no history file, so parallel runs in one directory
  don't interfere. Continuity is explicit — `--seed` prepends a captured transcript as
  premise. There is no per-directory conversation state.
- **Multimodal Support**: Image insertion (JPG, PNG) with DataContent handling for vision-capable models
- **Provider Architecture**: Pluggable AI providers via IChatClientProvider interface, supporting any Microsoft.Extensions.AI compatible provider

## AI Provider Plugin Architecture
- **nb.Providers.Abstractions** - Lightweight interface library containing only `IChatClientProvider` interface
- **ProviderManager** - Discovers and loads providers from `providers/` directory using `AssemblyLoadContext` for proper isolation
- **Directory Isolation** - Each provider lives in its own subdirectory with separate `AssemblyLoadContext` to prevent version conflicts
- **Configuration** - Providers configured via `ActiveProvider` + `ChatProviders` array in appsettings.json (see Configuration Schema below)
- **No Built-in Providers** - All providers are external plugins (AzureOpenAI, Anthropic, OpenAI, Gemini, etc.) loaded at runtime
- **Runtime Discovery** - Providers are loaded at startup with graceful error handling for missing dependencies
- **Post-Build Deployment** - Provider projects auto-copy their output to `bin/{Config}/net10.0/providers/{name}/` via post-build events
- **⚠️ Assembly Context Gotcha** - Shared types across different `AssemblyLoadContext` instances can cause type mismatch issues. Keep provider interface communication simple and avoid passing complex objects between providers and main app beyond the `IChatClient` interface.
- **⚠️ CRITICAL: Provider Exclusions** - When adding new provider projects, you MUST add exclusions to `nb.csproj` to prevent the provider files from being included in the main project. Add three exclusion entries for each provider directory:
  ```xml
  <Compile Remove="Providers\{ProviderName}\**" />
  <EmbeddedResource Remove="Providers\{ProviderName}\**" />
  <None Remove="Providers\{ProviderName}\**" />
  ```
  Failure to add these exclusions will cause namespace conflicts, assembly resolution issues, and build errors.

## MCP Implementation Details
- **McpManager.cs** - Manages MCP client lifecycle and exposes tools/resources via interfaces
- **Tool Integration** - MCP tools are automatically integrated with Microsoft.Extensions.AI tool system
- **Error Handling** - Graceful fallback when servers don't support prompts (some MCP servers are tools-only)
- **Configuration** - MCP servers configured in `mcp.json` with command, args, and environment variables. There is no `mcp.json` at the repo root — it resolves in layers (executable dir, then `~/.config/nb/`, then the nearest `.nb/mcp.json` walking up from cwd), later winning by server name. `--mcp` overrides with a single hermetic manifest. Template: `mcp.example.json`
- **Transport** - `StdioClientTransport` for process-based (`stdio`) servers; `HttpClientTransport` for remote (`http`) servers
- **HTTP auth headers** - HTTP servers accept a `headers` object on `McpServerConfig`. Values support `${VAR}` env interpolation via `McpManager.ResolveHeaders` (keys are not interpolated; unset vars warn and resolve to empty), matching the `${VAR}` convention in `ConfigurationService`. Keeps tokens out of the committed `mcp.json`.

## Built-in MCP Server
- **Location** - `mcp-servers/mcp-tester/` - Self-contained C# MCP server
- **Dynamic Prompts** - Can generate prompts from `.md` files in `Prompts/` directory
- **Parameter Support** - Supports up to 3 parameters using `{parameter}` syntax in markdown files
- **Tools** - Includes basic test tools (echo, reverse-echo, current-time)
- **Integration** - Added to solution file, builds alongside main project
- **Usage** - Configure in `mcp.json` with dotnet run command pointing to the project

## Bash Tool (Shell Integration)
Native tool that gives the model shell access under a declarative approval policy.

**Files:**
- `nb.Core/Shell/ShellEnvironment.cs` - Detects OS, shell, architecture, available tools at startup
- `nb.Core/Shell/BashTool.cs` - Executes commands with timeout, output truncation (sandwich strategy)
- `nb.Core/Shell/CommandClassifier.cs` - Classifies commands (read/write/delete/run) for approval display
- `nb.Core/Shell/ApprovalPatterns.cs` - Glob matching for bash auto-approve patterns, fed by `approval bash` directives, `Approval.Bash` in config, and `NbOptions.ApprovePatterns` for library hosts

**Features:**
- Environment context injected into system prompt (OS, shell, available tools, cwd)
- Command classification shown on the console (e.g., "read: /etc/hosts" vs "run: yarn install") — an observation channel for a human reading the run, not an approval prompt
- Dangerous command detection with warnings (sudo, rm -rf, etc.)
- Output sandwich truncation for large outputs (head + tail + stats)

**Usage:** approval is declarative, not a flag. A program says `approval bash git *`;
config says `Approval.Bash`. There is no `--approve` and no bare-prompt invocation:

```bash
printf 'approval bash ls *\nrun list the files here\n' | ./nb -
```

**Two-directory model:**
- `launchDirectory` - where nb started (immutable)
- `shellCwd` - where commands execute, can change via `set_cwd` tool

## Native File Tools
Cross-platform file tools that don't require shell access. All read-only tools auto-execute with no approval.

**Files:**
- `nb.Core/Shell/ReadFileTool.cs` - Read file contents (text with line numbers, PDF text extraction, image base64 for vision)
- `nb.Core/Shell/WriteFileTool.cs` - Create or overwrite files (requires approval unless trusted)
- `nb.Core/Shell/EditFileTool.cs` - Targeted string replacement in files (requires approval unless trusted)
- `nb.Core/Shell/FindFilesTool.cs` - Glob-based file discovery using `Microsoft.Extensions.FileSystemGlobbing`
- `nb.Core/Shell/GrepTool.cs` - Regex content search across files (supports content and files_with_matches output modes)
- `nb.Core/Shell/ListDirTool.cs` - Lightweight directory listing (files and subdirectories)
- `nb.Core/Shell/FileReadTracker.cs` - Tracks file reads; enforces read-before-edit/write and detects external modifications

**Auto-skipped directories:** `.git`, `node_modules`, `bin`, `obj`, `.vs`, `__pycache__`, `.venv`, `venv`, `.idea`, `dist`, `build`, `.next`, `.nuget`

No files are skipped by name. nb is stateless per-directory — it writes no
conversation history, lock, or kit state — so there is nothing of its own for
discovery to filter out. See `bugs/nb_State_Files_Leak_Into_Discovery.md`.

## nb does not confine the tools it runs

**nb is not a security boundary and has no filesystem sandbox.** The bash tool runs
the model's command string through `bash -c` with no OS-level isolation, so a model
driving it can read any file the nb process user can read. The approval ladder is a
C# string/path heuristic: it decides what gets *recorded and refused*, not what is
*possible*. A denylist cannot bound reads — block `cat`, and `awk`, `od`, `python -c`
and hundreds of other binaries still read files.

This is **accepted by design**, not a defect awaiting a fix. See
`bugs/shell-tool-no-filesystem-sandbox.md` (closed as accepted) and
`plans/approval-is-not-a-boundary.md` (accepted 2026-09-05) for the argument.

**The container is the boundary.** The standard deployment for an untrusted or
adversarial workload is: one container, nb running *inside* it, one filesystem.
Confining only bash while nb's in-process file tools (`read_file`, `edit_file`,
`find_files`, `grep`, `list_dir`) see the host would give the model two filesystems
and two sets of paths — which is why nb does not do it.

Two properties follow from nb being inside, and both matter when building harnesses:

- nb **shares a filesystem** with the model under test — `appsettings.json`, seeds
  and nb's binaries are all readable. The container should hold the fixture and
  nothing you would mind the model reading.
- nb **shares a network namespace** with it. Whatever nb can reach, the model can
  reach, so egress should be as narrow as nb's own needs allow.

What approval *is* for: a recorded, transcript-visible observation of what the model
reached for. `tool_call.approved` and the `denied` count in the result trailer are
the load-bearing outputs, and a denial is a datum about model behaviour rather than
a control.

## Trust Mode — a convenience default, not a boundary
Auto-approves file tools and non-dangerous bash commands whose paths fall under the
working directory. **This is a UX default, not a sandbox** — its shape is inherited
from nb's coding-agent era, where it kept an agent out of a watching human's home
directory. It is the right default for the REPL, where that human exists. It is the
wrong one inside a container, where it only produces false denials on legitimate
work (see `plans/approval-is-not-a-boundary.md` §1b).

**Activation:** `"Trust": true` in appsettings.json, or `NbOptions.Trust` for library hosts. There is no `--trust` flag — trust is a posture, set in config, not per-invocation. Note that `approval default deny` suppresses it (see `ApprovalPolicy.DecideBash`)

**Path scoping** (a convenience filter, not enforcement) — only auto-approves operations targeting:
- The shell cwd and subdirectories
- System temp directories (`/tmp`, `TEMP`/`TMP` on Windows)

**What gets auto-approved:**
- `write_file` / `edit_file` targeting paths under the working directory
- `bash` commands classified as non-dangerous with paths under the working directory
- `bash` Run commands with no extractable path (e.g. `dotnet build`, `git status`)

**What trust does NOT auto-approve** (these are denied unless something else allows them —
nothing prompts):
- Dangerous bash commands (rm -rf, sudo, etc.) — never auto-approved by trust
- File operations targeting paths outside the working directory
- MCP tools (use their own `alwaysAllow` mechanism)

Note that none of those denials *prevent* anything: a model that wants the same
effect can usually reach it by another command. They are recorded refusals.

**Other effects:** Bumps effective MaxToolCalls to 50 (minimum)

**Files:**
- `nb.Core/Shell/TrustSandbox.cs` - Static `IsPathTrusted` / `IsPathTrustedRelative` methods

## Coding Conventions
- Follow existing C# conventions in the codebase
- Use Spectre.Console markup for colored terminal output
- Handle exceptions gracefully with user-friendly error messages

## Development Best Practices
- When adding significant new features, or new configuration requirements, ask if you should update the readme.md
- Ask before adding an interface, unless there is an immediate, obvious reason to do so. Don't create new interfaces for "future flexibility."
- Avoid building DI scaffolding unless you're working with a library or package that expects you to use DI.

## Feature Documents
- `Features/` contains design docs for planned and implemented features
- These capture intent and reasoning, not current behavior - don't update them to match code
- When implementing: update Status to "Implemented", add PR link
- When revising significantly: add a "Revisions" section, don't rewrite history

## Architecture Notes
- **Microsoft.Extensions.AI Integration**: Uses modern AI abstractions with IChatClient interface for provider independence
- **Provider Abstraction**: LLM interactions isolated through pluggable provider system for easy swapping between AI services
- **Safety Mechanisms**: Max tool calls per message, parameter validation, graceful error handling
- **Clean Type System**: Uses Microsoft.Extensions.AI types (ChatMessage, ChatOptions, ChatResponse) throughout

## Configuration Schema
The application uses an array-based provider configuration schema:
```json
{
  "ActiveProvider": "AzureOpenAI",
  "ChatProviders": [
    {
      "Name": "AzureOpenAI",
      "Endpoint": "https://...",
      "ApiKey": "...",
      "ChatDeploymentName": "gpt-4"
    },
    {
      "Name": "Anthropic",
      "ApiKey": "...",
      "Model": "claude-3-7-sonnet"
    }
  ]
}
```
- `ActiveProvider` - Selects which `ChatProviders` entry to use, by its `Name`
- `ChatProviders` - Array of provider configurations
- `Name` - Free-form label for the entry. This is what `ActiveProvider` and the `provider` directive select by, and what per-entry lookups (`MaxContextTokens`, `Temperature`, `EditToolStyle`, prompt files) key off
- `Provider` (optional) - Name of the provider implementation backing the entry. Defaults to `Name`. Set it when several entries share one implementation — e.g. two `LocalLlm` servers on different ports:
  ```jsonc
  { "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "…:8081/v1", "Model": "qwen3-coder-next" },
  { "Name": "LocalAir",   "Provider": "LocalLlm", "Endpoint": "…:8082/v1", "Model": "glm-4.5-air" }
  ```
- `Headers` (optional) - Extra HTTP headers sent on every request, for a gateway that authenticates its caller with its own token: `"Headers": { "cf-aig-authorization": "Bearer ${CF_AIG_TOKEN}" }`. Values get the usual `${VAR}` expansion. A configured header *replaces* the SDK's own of that name. An entry carrying `Headers` needs no `ApiKey` (the stored-keys gateway mode). Implemented by `ProviderConfig` in `nb.Providers.Abstractions` — one `HttpClient` with a stamping `DelegatingHandler`, handed to each SDK's HTTP hook (`ClientOptions.HttpClient` for Anthropic, `ClientPipelineOptions.Transport` for the OpenAI/Azure family). Gemini's SDK has no such hook and does not support it
- Provider-specific fields are read directly from the provider's config object (no nested paths)
- Prompt files resolve by entry label first, then by implementation — `system.LocalCoder.md` if present, else `system.LocalLlm.md`. Same for the model-specific `system.{scope}.{modelslug}.md` form. Entry-label and implementation resolution live in `nb.Core/ProviderEntries.cs`
- `EditToolStyle` (**deprecated** 2026-08-15, optional, per-provider) - Selects the file-edit tool surface exposed to the model. `EditReplace` (default) advertises `edit_file` + `write_file`; `ApplyPatch` advertises `apply_patch` instead. Mutually exclusive — GPT-family models tend to confuse the two surfaces, so pick one. It affects *advertisement only*: every edit tool is always constructed, so a `harness` costume advertises its target's edit surface regardless of this setting. Still honored, but setting it emits a startup warning; `harness codex` replaces it and brings the whole Codex surface rather than a fragment of it. The plumbing lives in `NbRuntime.BuildAsync` → `NbHarness.ApplyPatchStyle` (see `plans/harness-emulation.md` step 7).

## Important Workflow Reminders
- When changing the structure of appsettings.json make sure to update appsettings.example.json

## Debugging Strategies
- When resolving code compilation problems, before attempting to refactor, check to see if the project is missing a dependency
- Don't add nuget packages or attempt to alter/update the version of an installed package by modifying csproj files

## Project substrate (managed by `imp init`)

This repo uses a structured project-knowledge substrate split between
the repo root (human-owned) and `imp/` (gnome-maintained). Read from
it before answering questions about design, intent, or current
behavior. You may edit human-owned files directly. imp writes its
own dir (`imp/*`) directly under a distinct git author
(`imp-gnome`); for cross-boundary edits to `rules/`, `plans/`,
`bugs/`, `TODO.md` it produces proposals you (or the human) review
via `/imp-promote`.

### What's where

Human-owned (repo root):

- **`plans/`** — design intent, specs, things to build. Flat; no
  active/archive split. Most new design work starts here.
- **`bugs/`** — bug reports.
- **`TODO.md`** — running todo list.
- **`rules/`** — hard project invariants. Substrate-shaped
  (frontmatter with `touches:`) so the gnome can flag drift, but
  authored directly by humans, not via stash. Code violating these
  is a bug.

Gnome-maintained (`imp/`):

- **`imp/log.md`** — append-only chronological history of substrate
  decisions, sweep findings, architectural changes.
- **`imp/learnings/`** — discovered knowledge, distilled by the
  gnome from stash items. Decays in relevance, not in truth.
- **`imp/reference/`** — archived external sources.
- **`imp/concepts/<topic>.md`** — auto-generated synthesis pages.
  Don't hand-edit; regenerated by `imp tidy`.
- **`imp/_index/`** — auto-generated query layout
  (`by-file/`, `by-symbol/`, `by-feature/`). Read these via your
  normal `Read`/`Glob`/`Grep`.
- **`imp/stash/`** — write target for `imp stash`. Gnome processes
  overnight.
- **`imp/_meta/`** — substrate conventions.

Gitignored:

- **`.imp/`** — layer-0 structural cache (tree-sitter symbol map,
  repo-map, manifests). Build artefact, regenerable.

For drift semantics per kind, see `imp/_meta/conventions.md`.

### imp proposals

imp produces proposals at `nb.imp-proposals/P-NNN-<slug>.md`
when scheduled sweeps detect cross-boundary changes (promotion
candidates, drift, doc rot, etc.) for the human-owned dirs. Apply
via `/imp-promote`. Auto-approval gradient when reviewing on the
user's behalf:

- **Always-safe** (auto-apply): `log.md` appends, archive moves.
- **Claude-approvable**: new learning entries, concept regeneration,
  candidate flags.
- **Human-required**: rules edits, deletions, supersede markers,
  anything that loses information.