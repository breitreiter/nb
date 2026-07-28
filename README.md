# NotaBene (nb)

A terminal-native **conversation-program evaluator** with deep shell integration, native file tools, and pluggable AI providers.

![NotaBene Preview](preview.png)

nb runs a **conversation-program**: one ordered directive document that carries provider, model, tool surface, approval policy, fabricated history, and the live prompt — so *one document is one runnable program*. It is not a chat client. Give it a program file (or pipe one on stdin), or drop into a REPL that interprets the same source syntax line by line. Every invocation is stateless; continuity is explicit (`--seed`). nb is also consumable in-process as a .NET library (`nb.Core` → `Nb.RunAsync`).

Full reference: [`docs/conversation-program-cli.md`](docs/conversation-program-cli.md) (CLI) and [`docs/conversation-program-api.md`](docs/conversation-program-api.md) (library).

## Features

- **Conversation Programs**: Configuration, tool surface, approval policy, fabricated history, and the prompt as one directive document — run from a file, stdin, or the REPL. Machine-readable output (`--output jsonl`/`porcelain`) is the same schema `--seed` reads, so record → edit → replay is native.
- **Multi-Provider AI Support**: Built-in support for Azure OpenAI (Chat Completions and Responses API), OpenAI, Anthropic Claude, and Google Gemini. Bring any Microsoft.Extensions.AI compatible model.
- **Program REPL**: `nb` on a TTY starts a live interpreter of the source syntax — the authoring/debug surface, not a chat loop.
- **Native File + Shell Tools**: Cross-platform `bash`, `read_file`, `write_file`, `edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`, `fetch_url`, with a read-before-edit guard and a declarative approval policy (`approval` directives + config).
- **Bubblewrap Sandbox**: `approval sandbox bwrap` runs the bash child in a locked-down namespace (Linux).
- **File Insertion** (PDF, TXT, MD, JPG, PNG) with multimodal support for vision-capable models
- **MCP Server Integration** (stdio + HTTP transports) for extensible tools and resources
- **In-process library**: reference `nb.Core` and call `Nb.RunAsync(config, program, options)`.
- **Project Context**: `@file` includes and `NB.md` for project-specific context

## Prerequisites

- .NET 10.0 or later
- API key for at least one supported AI provider:
  - Azure OpenAI
  - OpenAI
  - Anthropic Claude
  - Google Gemini

## Installation

### Requirements

- .NET 10 SDK (to build from source) or .NET 10 runtime (for pre-built binaries)
- **Windows only:** [Git for Windows](https://git-scm.com/download/win) — nb uses Git Bash for its shell tool on Windows. PowerShell is not supported, because models mix bash and PowerShell idioms when given a tool named `bash` and produce broken commands. If `bash.exe` isn't found at install time, nb will tell you where to get it.

### Option 1: Build from Source (Recommended)

1. Clone and configure:
   ```bash
   git clone https://github.com/breitreiter/nb
   cd nb
   cp appsettings.example.json appsettings.json
   ```

2. Edit `appsettings.json` with your AI provider configuration.

3. Build and run:
   ```bash
   dotnet build
   cd bin/Debug/net10.0
   ./nb
   ```

   **Note:** nb must run from the bin directory where provider DLLs are located.

### Option 2: Pre-built Binaries

Pre-built binaries are available in the [releases section](https://github.com/breitreiter/nb/releases), but they are not code-signed. This means you'll encounter security warnings on both Windows and macOS.

#### Windows

Windows Defender SmartScreen will warn you about running an unsigned application. Click "More info" then "Run anyway" to proceed. See Microsoft's [SmartScreen documentation](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/) for more information.

#### macOS

macOS Gatekeeper will block unsigned applications. See Apple's guide on [safely opening apps on your Mac](https://support.apple.com/en-us/102445) for instructions on how to run unsigned applications.

## Configuration

After installation, configure nb for your environment:

1. **AI Provider**: Edit `appsettings.json` with your API keys and endpoints. You can configure multiple providers and switch between them at runtime, but you only need to start with one. nb supports local models via HTTP. If your model doesn't have a standard context window size, you'll need to set the `MaxContextTokens` value in `appsettings.json`. nb ships with several prompt extensions for common model, but you can also add your own.

2. **MCP Servers** (Optional): Copy `mcp.example.json` to `mcp.json` and configure your MCP server connections.

3. **Theme** (Optional): Customize colors by editing `theme.json`.

#### Config resolution

Configuration resolves in layers, later winning: install defaults (`appsettings.json` next to the binary) → user config (`~/.config/nb/config.json`, honoring `XDG_CONFIG_HOME`) → the nearest project `.nb/config.json` (found by walking up from the current directory) → `NB_`-prefixed environment variables (`NB_ActiveProvider`, `NB_ChatProviders__0__ApiKey`, …). This keeps API keys out of the install directory and lets a project or CI job set provider/model without editing shared config. Pass `--config <file>` to use a single config file hermetically (ignoring the layers) — handy for isolated test runs.

For the common knobs there are friendly env aliases, so CI doesn't need the raw nested paths: `NB_PROVIDER` (the active provider), `NB_MODEL` (the active provider's model), and `NB_OUTPUT` (the default output mode). `mcp.json` resolves in the same install → user (`~/.config/nb/mcp.json`) → project (`.nb/mcp.json`) layers, merging server definitions by name — so a project can add or override MCP servers without editing the install manifest (`--mcp <file>` still selects a single manifest hermetically).

## Usage

nb runs a program. Give it a file, pipe one on stdin, or start the REPL:

```bash
nb flow.nb                          # run a program file
echo 'run summarize this' | nb -     # run a one-off program from stdin
nb                                   # (on a TTY) start the program REPL
```

There is **no bare-prompt mode** — `nb "some text"` is read as a program *file* named "some text". To run a quick prompt, wrap it as a program: `echo 'run some text' | nb -`.

**The REPL** interprets the same source syntax line by line — each line is a directive, `run` invokes the model, Ctrl-D exits. It is the authoring/debugging surface, not a chat loop.

**Stateless + explicit continuity.** nb reads and writes no history file, so parallel runs just work. Carry continuity with `--seed`, which prepends a captured transcript as premise:
```bash
echo 'run start a haiku about autumn' | nb - --output jsonl > turn1.jsonl
echo 'run now finish it' | nb - --seed turn1.jsonl
```

nb exposes the current working directory as an MCP root, to help filesystem MCP servers orient themselves.

### Machine-Readable Output

A program defaults to `--output jsonl`. Both machine modes route the transcript to stdout and all chrome (tool logs, warnings) to stderr, so a script captures a clean result:

```bash
nb flow.nb                       # jsonl: a typed event stream (user/assistant_text/tool_call/… + a result trailer)
nb flow.nb --output porcelain    # plain text: TOOL/RESULT lines + the answer verbatim (fenced blocks survive)
```

Color is disabled automatically when stdout is redirected or `NO_COLOR` is set. The process exit code is meaningful: `0` success, `2` provider error, `3` turn aborted (tool-call budget or repeated failures), `4` approval denied.

### Conversation Programs

A **conversation-program** is an ordered list of directives that builds a conversation and invokes the model — nb's scripting surface, where configuration, fabricated history, and the live prompt are one document instead of three mechanisms.

```
provider anthropic
model claude-sonnet-5
system you are a terse assistant
user what's 2+2?
assistant 4
run and what's the square of that?
```

```bash
nb flow.nb            # from a file
nb - < flow.nb        # from stdin (a first '{' char is read as jsonl bytecode instead)
```

Each line is `<verb> <content>`. **Config directives** (`provider`, `model`, `mcp`, `tools`, `approval`) set the envelope going forward; **turn directives** (`system`, `user`, `assistant`) append messages; **`run`** invokes the model on the accumulated state (`run <text>` is shorthand for a `user` turn then `run`). (Output format is the `--output` flag, not a directive — it's caller delivery, not program logic.) Because config directives can appear between runs, one file can drive two models:

```
model haiku
run quick triage of this log
model opus
run now analyze the root cause
```

A trailing `\` continues content onto the next line, `#` lines are comments, and `@file` as a directive's whole content includes that file. A program is never given nb's default persona — it gets exactly the `system` directives it writes (nothing, if it writes none), which is what an eval harness wants. Programs default to `--output jsonl`.

A JSONL (bytecode) program can additionally fabricate a **tool round** as premise — `tool_call` and its matching `tool_result` events — which is loaded into history exactly as a `--seed` transcript is (a turn's assistant text and its calls batch into one message; every call must have a result before the run that consumes it). These aren't live invocations; they're recorded rounds you're replaying into the model's view. The source syntax has no verb for them.

The `tools` and `mcp` directives reshape the tool surface, with delta tokens (`+name`, `-name`, or the lone `none`):

```
tools -bash        # drop one native tool (bash, read_file, write_file, edit_file, find_files, grep, list_dir, apply_patch, fetch_url)
tools none         # no native tools this run
mcp +figma         # expose the figma MCP server's tools
```

Native tools are **all-on** by default; a `tools` directive filters them. MCP servers are **strict-empty**: a program exposes no MCP tools unless it names servers with `mcp +server`. `--resolve` prints the resolved surface at each run point.

The `approval` directive sets the approval policy — which tool calls auto-approve, and what an unmatched call does:

```
approval bash git status   # auto-approve bash commands matching this pattern
approval mcp weather/*      # auto-approve MCP tools matching this glob ('/' aliases the '_' in weather_current)
approval default deny       # refuse any unmatched call outright (instead of prompting)
approval sandbox bwrap      # run the bash child under a bubblewrap sandbox (Linux)
```

These layer onto the `Approval` config block (`Bash`/`McpTools`/`Default`/`Sandbox` in `appsettings.json`), which does the same thing outside a program. In a **headless** run (piped stdin) every unmatched call is already denied, so the allow-lists are what make a scripted run auto-approve exactly the tools it needs; `approval default deny` adds the same lockdown to an interactive session. `--resolve` prints the effective policy per run point.

The **bash sandbox** (`approval sandbox bwrap`, or `Approval.Sandbox` in config) wraps the bash child in a [bubblewrap](https://github.com/containers/bubblewrap) namespace: the whole filesystem is read-only, only the current directory and a fresh `/tmp` are writable, known secret dirs (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/nb`) are masked to empty, and there's no network. Use `bwrap-net` to keep the sandbox but allow network. It contains only bash — MCP and `fetch_url` run in-process under their own approval. Requesting `bwrap` on a host without bubblewrap (non-Linux, or `bwrap` not on `PATH`) hard-fails the run.

Reuse across programs is composition, not a flag: factor shared directives into a file and pull them in with `@file` includes.

**Inspect a program without running it:**

```bash
nb --validate flow.nb    # parse + check (unknown provider, bad approval directive); exit 1 on error
nb --resolve  flow.nb    # print the effective envelope at each run point
```

The program format and the transcript format are the same schema: `--output jsonl` emits it and `--seed` loads it, so record → edit → replay is native.

### Tools and approval

Native tools (all-on by default; filter with `tools`): `bash`, `read_file`, `write_file`, `edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`, `fetch_url`. `edit_file`/`write_file` enforce a **read-before-edit guard**; read-only file tools auto-approve inside the working-directory sandbox (cwd + system temp), paths outside do not.

Approval is a **declarative policy**, not interactive prompts (a program run is headless — an unmatched tool call is denied, not prompted). Set it with `approval` directives in the program or the `Approval` block in config:

```
approval bash "git status"     # auto-approve a matching bash command
approval mcp weather/*          # auto-approve matching MCP tools
approval default deny           # refuse any unmatched call outright
approval sandbox bwrap          # run the bash child under a bubblewrap sandbox (Linux)
```

Some commands are always safe (auto-approved): build tools (`dotnet build`, `cargo build`, `make`, `npm run`, …), read-only git (`git status`/`log`/`diff`/`show`), and read-only queries (`which`, `file`, …). A **trust posture** (`"Trust": true` in config) auto-approves non-dangerous tools within the cwd sandbox and bumps the max tool calls to 50; dangerous commands (`rm -rf`, `sudo`) never auto-approve.

See [Conversation Programs](#conversation-programs) for the full `approval` / `tools` / sandbox semantics.

`--dump-tools` writes the connected MCP tool manifest to `mcp-tools.json`; `--resolve` prints the tool surface a program exposes at each run point.

### Command-Line Flags

Flags vary how a program runs; they never replace or duplicate a program verb. Trust, no-tools, bash auto-approve, provider, and model are program concerns (`approval`/`tools`/`provider`/`model` directives, or config), not flags.

| Flag | Description |
|------|-------------|
| `--output <mode>` | `jsonl` (default for a program), `porcelain`, or `interactive` — see [Machine-Readable Output](#machine-readable-output) |
| `--seed <file>` | Prepend a jsonl transcript as premise history before the program runs |
| `--config <file>` | Use a single config file hermetically, ignoring the layered resolution |
| `--mcp <file>` | Use a single MCP manifest hermetically, ignoring the layered resolution |
| `--validate` | Parse and check a program, run nothing (exit 1 on error) |
| `--resolve` | Print the effective envelope at each run point, run nothing |
| `--verbose` | Log tool call inputs and outputs (useful for debugging) |
| `--dump-tools` | Write MCP tool manifest to `mcp-tools.json` and exit |

The program itself is the positional argument (`nb flow.nb`) or stdin (`nb -`).

### Switching providers
A program selects its provider/model with the `provider` and `model` directives, and can switch between runs (`model haiku` / run / `model opus` / run) within one document — see [Conversation Programs](#conversation-programs). Connection (endpoint + key) stays in config; only the non-secret model name travels in the program.

The menu lists `ChatProviders` entries, not implementations. An entry's `Name` is a free-form label; the optional `Provider` field names the implementation behind it, so several entries can share one — useful when a single implementation fronts multiple backends, as `LocalLlm` does for local servers:

```jsonc
{ "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1", "Model": "qwen3-coder-next" },
{ "Name": "LocalAir",   "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8082/v1", "Model": "glm-4.5-air" }
```

Omit `Provider` and it defaults to `Name`, which is how every single-backend entry above is written.

### MCP Configuration
Configure MCP servers in `mcp.json`:
```json
{
  "servers": {
    "my-server": {
      "type": "stdio",
      "command": "my-mcp-server",
      "args": ["--some-flag"],
      "alwaysAllow": ["tool1", "tool2"]
    }
  }
}
```

The `alwaysAllow` array specifies tools that skip approval prompts. Use `["*"]` to auto-approve all tools from a server (useful for automation):
```json
"alwaysAllow": ["*"]
```

#### HTTP servers and auth headers
For remote servers, use `"type": "http"` with an `endpoint`. Supply auth via a `headers` object; values support `${VAR}` interpolation against environment variables, so tokens stay out of the committed `mcp.json`:
```json
"figma": {
  "type": "http",
  "endpoint": "https://mcp.figma.com/mcp",
  "headers": {
    "Authorization": "Bearer ${FIGMA_TOKEN}"
  }
}
```
Only header values are interpolated (not keys). A referenced variable that isn't set logs a warning and resolves to an empty string. Literal values (no `${...}`) work too.

### Built-in MCP Server
The project includes a test server (`mcp-servers/mcp-tester/`) with basic tools.

### Fake Tools
nb will read `fake-tools.yaml` and treat those definitions as normal tools. When the model requests a fake tool, nb will return the configured response. Refer to `fake-tools.example.yaml` for the expected format.

Fake tool definitions will override MCP definitions. This is by design, to allow you to fake destructive actions or quickly tune tool descriptions for alignment testing.

#### Response Macros
Responses support macros for dynamic values, so each invocation produces fresh data instead of identical static strings:

| Macro | Description | Example |
|-------|-------------|---------|
| `{{$guid}}` | Random UUID | `a3b1c2d4-...` |
| `{{$timestamp}}` | Current UTC time (ISO 8601) | `2026-02-25T14:30:00Z` |
| `{{$int}}` | Random integer | `483291` |
| `{{$int(1,100)}}` | Random integer in range | `42` |
| `{{$counter.name}}` | Auto-incrementing counter | `1`, `2`, `3`... |
| `{{$param.fieldname}}` | Echo back a tool argument | value of `fieldname` |
| `{{$choice(a,b,c)}}` | Random pick from list | `b` |
| `{{$random_string}}` | Random alphanumeric (8 chars) | `xK9mPq2r` |
| `{{$random_string(16)}}` | Random alphanumeric (custom length) | `xK9mPq2rT5nLw8yZ` |

Example response template:
```yaml
response: '{"id": "{{$guid}}", "status": "{{$choice(pending,active,completed)}}", "created_at": "{{$timestamp}}"}'
```

## Project Context

nb injects no implicit context (there's no persona floor). A program includes what it needs explicitly — pull a project-context file into a `system` directive with an `@file` include:

```
system @./NB.md
run review the staged changes
```

`@file` resolves relative to the program file, so a program can compose shared context and directives from files instead of a flag.

## Theming

nb loads its color scheme from `theme.json` at startup. Color names are from [Spectre.Console](https://spectreconsole.net/appendix/colors)

For example, here's a high-contrast theme (WCAG AAA on standard Windows console background #0C0C0C):

```json
{
  "Success": "lime",
  "Error": "red",
  "Warning": "yellow",
  "Info": "white",
  "Muted": "grey70",
  "Accent": "aqua",
  "UserPrompt": "lime",
  "FakeTool": "magenta"
}
```

## Building for Distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Include `mcp.json` and `theme.json` with your executable for custom configurations. Providers deploy to `providers/` next to the binary.

## AI Provider Architecture

nb includes several built-in AI providers and supports extensibility for additional services:

### Built-in Providers
- **AzureOpenAI** - Chat Completions on classic Azure OpenAI resources
- **AzureFoundry** - Responses API on classic Azure OpenAI resources (needed for codex-family models like `gpt-5-codex`, and any other Responses-API-only model)
- **OpenAI** - Direct OpenAI API integration
- **Anthropic** - Claude models with function calling support
- **Google Gemini** - Google's generative AI models
- **Mock** - Testing provider that requires no API key

#### Which Azure provider do I want?

Azure's product surface spans several ways to expose a model. If you're unsure, match the API shape your deployment exposes:

| Your deployment URL looks like... | Use provider |
|---|---|
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/deployments/<name>/chat/completions?...` | `AzureOpenAI` |
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/responses?...` | `AzureFoundry` |

Both providers accept either the resource root (`https://<name>.cognitiveservices.azure.com/`) or the full deployment URL in the `Endpoint` field — the plugin strips to the host. The `Model` field is your **deployment name** (what you named it at deploy time), not the model family name.

If Azure shows you an endpoint on `services.ai.azure.com` with a `/api/projects/<project>/...` path, that's the newer Foundry Unified Endpoint and neither current provider targets it directly — open an issue if you need that variant.

All providers are automatically compiled into the `bin/{Config}/net10.0/providers/` directory during build.

The Mock provider returns "OK" by default, or the value of the `Response` config key. You can also control responses inline by prefixing your message with `MOCK:response=<text>`.

### Provider Extensibility

nb uses a pluggable provider architecture built on Microsoft.Extensions.AI. The repo includes 4 common providers, but you can roll your own.

1. Create a new project and add the NuGet package:
   ```bash
   dotnet add package nb.Providers.Abstractions
   ```
2. Implement the `IChatClientProvider` interface, which requires you to supply an instance of `IChatClient` from Microsoft.Extensions.AI plus some basic configuration tooling.
3. Build and copy your assembly to a new subdirectory under `providers/`
4. Add any required configuration to appsettings.json.

See the [nb.Providers.Abstractions](https://www.nuget.org/packages/nb.Providers.Abstractions) package for full documentation and examples.

## License

MIT License
