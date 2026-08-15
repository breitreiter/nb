# NotaBene (nb)

**A small-program evaluator for LLM automation** — evals, model comparison, prompt
regression, and tool-use testing, driven from one file you can check into git.

nb runs a **conversation-program**: one ordered directive document carrying
provider, model, tool surface, approval policy, fabricated history, and the live
prompt — so *one document is one runnable program*. Every invocation is stateless,
output is machine-readable by default, and the output schema is the same schema
the input accepts, so record → edit → replay is native.

```
# triage.nb
provider anthropic
model claude-sonnet-5
system you are a terse triage assistant. answer with one word: bug, feature, or noise.
user the login button is 3px off center on Safari
assistant bug
run the docs mention a --fast flag that does not exist
```

```bash
$ nb triage.nb --output porcelain
bug
```

A run is exactly what its document says: the history it fabricates, the persona it
writes, the tools it declares. Nothing is injected, and nothing carries over from the
last invocation, which is what makes a scripted unit of LLM work cheap to run a
thousand times and easy to inspect afterwards. A program can also put the run in
another agent's costume with `harness` (see [Harness costumes](#harness-costumes)), to
measure a model through the surface it was trained against.

Full reference: [`docs/conversation-program-cli.md`](docs/conversation-program-cli.md)
(CLI/subprocess) and [`docs/conversation-program-api.md`](docs/conversation-program-api.md)
(in-process library).

## What it's for

- **Evals** — a program per case, `--output jsonl` into your scorer. No history file,
  so cases run in parallel without stepping on each other. nb's own eval suite
  (`evals/run.sh`) is written this way against the Mock provider.
- **Model comparison** — same program, `provider`/`model` swapped, or swapped
  *mid-document* so one run hands off from a cheap model to an expensive one.
- **Prompt regression** — the program is a text file: diff it, review it, bisect it.
- **Tool-use and alignment testing** — declare a tool surface (`tools`, `mcp`),
  fabricate a prior tool round the model believes it already made, or define
  `fake-tools.yaml` entries so "destructive" tools return canned results instead of
  doing anything.
- **Harness comparison** — run one model through `harness codex` and `harness
  claude-code` and diff the transcripts. `harness` is a program directive, so the
  experiment lives in two files in a directory instead of a config edit between runs.
- **A subroutine inside a bigger agent** — call `Nb.RunAsync` in-process and get a
  typed result, or spawn `nb` as a subprocess and parse stdout.

The shell and file tools are a **tool surface you hand the model under test** — shaped
per program by `tools` and `mcp`, governed by declarative approval policy, and
optionally confined to a bubblewrap sandbox.

## Prerequisites

- .NET 10 SDK (to build from source) or .NET 10 runtime (for pre-built binaries)
- API key for at least one supported provider — Azure OpenAI, OpenAI, Anthropic,
  Google Gemini, or any local server on the OpenAI wire. (The **Mock** provider
  needs no key, and is enough to develop programs against.)
- **Windows only:** [Git for Windows](https://git-scm.com/download/win) — nb uses Git
  Bash for its shell tool on Windows. PowerShell is not supported, because models mix
  bash and PowerShell idioms when given a tool named `bash` and produce broken
  commands. If `bash.exe` isn't found at install time, nb will tell you where to get it.

## Installation

### Option 1: Build from Source (Recommended)

```bash
git clone https://github.com/breitreiter/nb
cd nb
cp appsettings.example.json appsettings.json   # then edit in your provider config
dotnet build
cd bin/Debug/net10.0
echo 'run MOCK:response=hello' | ./nb -
```

**Note:** nb must run from the bin directory, where the provider DLLs live.

### Option 2: Pre-built Binaries

Pre-built binaries are in the [releases section](https://github.com/breitreiter/nb/releases),
but they are not code-signed, so you'll hit security warnings. On Windows, SmartScreen:
click "More info" → "Run anyway" ([docs](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/)).
On macOS, Gatekeeper: see Apple's guide on [safely opening apps](https://support.apple.com/en-us/102445).

## Configuration

Configuration holds **connection** (endpoints, keys) and defaults. Everything about a
particular run — which model, which tools, what's allowed — belongs in the program.

1. **Providers**: edit `appsettings.json` with keys and endpoints. Configure as many
   as you like; a program picks one by name. If a model has a non-standard context
   window, set `MaxContextTokens` on its entry.
2. **MCP servers** (optional): copy `mcp.example.json` to `mcp.json`.
3. **Theme** (optional): colors in `theme.json`.

### Config resolution

Config resolves in layers, later winning: install defaults (`appsettings.json` next to
the binary) → user config (`~/.config/nb/config.json`, honoring `XDG_CONFIG_HOME`) →
the nearest project `.nb/config.json` (walking up from the current directory) →
`NB_`-prefixed environment variables (`NB_ActiveProvider`, `NB_ChatProviders__0__ApiKey`, …).
This keeps API keys out of the install directory and lets a CI job set provider/model
without editing shared config. `--config <file>` uses a single file **hermetically**,
ignoring the layers — which is what you want for a reproducible test run.

Friendly env aliases for the common knobs: `NB_PROVIDER`, `NB_MODEL`, `NB_OUTPUT`.
`mcp.json` resolves in the same install → user (`~/.config/nb/mcp.json`) → project
(`.nb/mcp.json`) layers, merging server definitions by name; `--mcp <file>` selects a
single manifest hermetically.

## Running a program

```bash
nb flow.nb                           # run a program file
echo 'run summarize this' | nb -     # a one-off program on stdin
nb                                   # (on a TTY, no input) the program REPL
```

The positional argument is a program **file**, so `nb "some text"` goes looking for a
file named "some text". To run a one-off prompt, wrap it in a `run`:
`echo 'run some text' | nb -`.

**Stateless + explicit continuity.** nb reads and writes no history file, so parallel
runs just work. Carry continuity with `--seed`, which prepends a captured transcript
as premise:

```bash
echo 'run start a haiku about autumn' | nb - --output jsonl > turn1.jsonl
echo 'run now finish it'              | nb - --seed turn1.jsonl
```

nb exposes the current working directory as an MCP root, to help filesystem MCP
servers orient themselves.

### Machine-readable output

A program defaults to `--output jsonl`. Both machine modes route the transcript to
**stdout** and all chrome (tool logs, warnings) to **stderr**, so a script captures a
clean result:

```bash
nb flow.nb                       # jsonl: a typed event stream (user/assistant_text/tool_call/… + a result trailer)
nb flow.nb --output porcelain    # plain text: TOOL/RESULT lines + the answer verbatim (fenced blocks survive)
nb flow.nb 2>/dev/null | jq -r 'select(.type=="assistant_text").text'
```

Color is disabled automatically when stdout is redirected or `NO_COLOR` is set. Exit
codes are meaningful: `0` success, `2` provider error, `3` turn aborted (tool-call
budget or repeated failures), `4` approval denied — so a caller can classify failures
without parsing text.

The program format and the transcript format are the **same schema**: `--output jsonl`
emits it and `--seed` loads it.

## The program format

Each line is `<verb> <content>`:

- **Config directives** (`provider`, `model`, `harness`, `mcp`, `tools`, `approval`,
  `loop`, `budget`) set the envelope going forward.
- **Turn directives** (`system`, `user`, `assistant`) append messages.
- **`run`** invokes the model on the accumulated state (`run <text>` is shorthand for a
  `user` turn followed by `run`).

Output format is the `--output` flag, not a directive — it's caller delivery, not
program logic. Because config directives can appear between runs, one document can
drive two models:

```
model haiku
run quick triage of this log
model opus
run now analyze the root cause
```

A trailing `\` continues content onto the next line, `#` lines are comments, and
`@file` as a directive's whole content includes that file (resolved relative to the
program file — so shared context and shared directives compose without a flag).

A program is **never given a default persona**. It gets exactly the `system`
directives it writes, and nothing if it writes none — which is what an eval wants, and
the main reason results here don't drift when nb changes. The sole exception is
explicit: a `harness` directive brings its costume's prompt with it.

### Fabricated history

`user`/`assistant` directives fabricate turns the model believes already happened —
few-shot examples, a primed state, a mid-conversation probe. A JSONL (bytecode)
program can additionally fabricate a **tool round**: `tool_call` and its matching
`tool_result` events, loaded into history exactly as a `--seed` transcript is (a
turn's assistant text and its calls batch into one message; every call must have a
result before the run that consumes it). These aren't live invocations — they're
recorded rounds you're replaying into the model's view. The source syntax has no verb
for them.

### Tool surface

The `tools` and `mcp` directives reshape the tool surface with delta tokens (`+name`,
`-name`, or the lone `none`):

```
tools -bash        # drop one native tool
tools none         # no native tools this run
mcp +figma         # expose the figma MCP server's tools
```

Native tools are **all-on** by default (`bash`, `read_file`, `write_file`,
`edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`, `fetch_url`,
`search_web`, `todo`); a `tools` directive filters them. MCP servers are
**strict-empty**: a program exposes no MCP tools unless it names servers with
`mcp +server`. `edit_file`/`write_file` enforce a read-before-edit guard; `read_file`
handles text (with line numbers), PDF text extraction, and images as base64 for
vision-capable models. `search_web` records search intent as an observable in the
transcript; `todo` is a steering aid (a task list plus a nudge for models prone to
abandoning work), and `tools -todo` removes it along with the nudge.

Those names are **canonical under every harness**. A costume changes the names the
*model* sees, not the names a program writes: under `harness claude-code` the model is
offered `Edit`, but the program still says `tools -edit_file` — because `tools` states
what the run may do, not what the model is shown. Writing a costume's wire name is an
error rather than a silent no-op, so `--validate` catches it instead of handing you a
tool you thought you had removed.

### Harness costumes

`harness <name>` selects the harness a run wears — its tool surface, its result
formatting, and its prompt. It defaults to `nb` (nb's own surface); the registered
costumes are `qwen-code`, `codex`, and `claude-code`. An unknown name is a parse error.
A run that quietly fell back to nb's surface while the program said `codex` would
produce comparative numbers that mean nothing.

```
harness codex
run refactor the parser in src/ and run the tests
```

A costume swaps what is *advertised*, never what is behind it: the same bash and file
tools, under the target's names and schemas. Under `codex` the model sees
`shell_command`, `apply_patch`, `update_plan`, and `view_image`; under `claude-code` it
sees `Bash`, `Read`, `Edit`, `Write`, `Glob`, `Grep`, `TodoWrite`, `WebFetch`,
`WebSearch`, and stubs for `Task`/`Skill`/`NotebookEdit`; under `qwen-code`,
`run_shell_command`, `edit`, `glob`, `grep_search`, and friends.

Naming a harness opts into the **whole** costume. A program that asks to imitate
another agent, and is then told it should *also* have requested the prompt, has been
failed by the tool. So a costume brings:

- its **prompt preamble** (Codex's is its own text, vendored under Apache-2.0; the
  qwen-code and claude-code preambles are nb-authored facsimiles that occupy the same
  channels in original prose, not transcriptions of closed text);
- the **project instruction files** its target reads — `AGENTS.md` under `codex`,
  `CLAUDE.md` under `claude-code` — collected from the repo root down to the working
  directory, in that harness's own wrapper;
- its **environment block** — cwd, shell or platform, date, git state — in the target's
  layout. (`qwen-code` sends none, deliberately.)

Everything injected lands in the transcript as ordinary `system` messages, so the wire
record is complete and a `--seed` replay reproduces it exactly even after the costume
changes. The trade is that a costume reads files and the clock, so a named-harness run
is reproducible **from its transcript, not from the program alone**. The same program
in two directories sends different text, because the harness being imitated does the
same.

Each costume also **reports what it knowingly does not reproduce**, as run warnings, so
a surprising result arrives with a suspect list attached: ignored arguments, stubbed
tools, result strings written from observed behaviour, and the parts of the target's
surface (sub-agents, skills, background shells, plugins) that are out of scope. Worth
reading before you draw a conclusion from a costumed run. The costume set is closed and
in-tree; design notes in [`plans/harness-emulation.md`](plans/harness-emulation.md).

### Loop and budget guards

Run-level ceilings, layered onto config, governing every run after them:

```
loop 5                  # doom-loop threshold: nudge after 5 repeated tool-call sequences ('loop off' disables)
budget tokens 200000    # session-cumulative token ceiling; abort with exit_reason token_budget
budget tool_calls 40    # per-turn tool-call cap for subsequent runs
budget wall_ms 120000   # session wall-clock ceiling; cancels the in-flight call
```

The doom-loop detector is a *soft* guard — it injects a `<system_reminder>` and the run
continues (on by default, threshold 3). The budgets are *hard* ceilings that abort with
exit 3, which is what bounds a runaway loop or a hung provider. All are additive: a
program that names none behaves as before.

### Approval policy

Approval is a **declarative policy**, never an interactive prompt — an unmatched tool
call is denied rather than asked about, in every mode including the REPL. nb does not
stop mid-run to collect authorization; a program states what it is allowed to do. Set it
with `approval` directives, or the `Approval` block (`Bash`/`McpTools`/`Default`/`Sandbox`)
in config:

```
approval bash git status   # auto-approve bash commands matching this pattern
approval mcp weather/*     # auto-approve MCP tools matching this glob ('/' aliases the '_' in weather_current)
approval search allow      # auto-approve search_web (most runs need this, see below)
approval fetch allow       # auto-approve fetch_url (separate grant from search)
approval default deny      # honour the explicit allow-list and nothing else
approval sandbox bwrap     # run the bash child under a bubblewrap sandbox (Linux)
```

Any run that means to search needs `approval search allow`. An unapproved tool can never
execute, so without it every `search_web` call reads as a denial. (The search intent is
recorded in the transcript either way.)

The allow-lists are what let a scripted run auto-approve exactly the tools it needs.
Some commands are always safe: build tools (`dotnet build`, `cargo build`, `make`,
`npm run`, …), read-only git (`status`/`log`/`diff`/`show`), and read-only queries
(`which`, `file`, …). A **trust posture** (`"Trust": true` in config) auto-approves
non-dangerous tools within the cwd sandbox (cwd + system temp) and bumps the max tool
calls to 50; dangerous commands (`rm -rf`, `sudo`) never auto-approve.

The **bash sandbox** (`approval sandbox bwrap`, or `Approval.Sandbox` in config) wraps
the bash child in a [bubblewrap](https://github.com/containers/bubblewrap) namespace:
the whole filesystem read-only, only the current directory and a fresh `/tmp`
writable, known secret dirs (`~/.ssh`, `~/.aws`, `~/.gnupg`, `~/.config/nb`) masked to
empty, and no network. Use `bwrap-net` to keep the sandbox but allow network. It
contains only bash — MCP and `fetch_url` run in-process under their own approval.
Requesting `bwrap` on a host without bubblewrap (non-Linux, or not on `PATH`)
hard-fails the run.

### Inspecting a program without running it

```bash
nb --validate flow.nb    # parse + check (unknown provider, bad approval directive); exit 1 on error
nb --resolve  flow.nb    # print the effective envelope — provider, model, harness, tool surface, policy — at each run point
```

`--validate` is cheap enough to run over a whole eval corpus in CI before spending
tokens on it.

### Command-line flags

Flags vary how a program is delivered and inspected; they never duplicate a program
verb. Which model, which tools, and what's allowed belong to the program
(`provider`/`model`/`tools`/`approval`) or to config.

| Flag | Description |
|------|-------------|
| `--output <mode>` | `jsonl` (default), `porcelain`, or `interactive` |
| `--seed <file>` | Prepend a jsonl transcript as premise history before the program runs |
| `--config <file>` | Use a single config file hermetically, ignoring the layered resolution |
| `--mcp <file>` | Use a single MCP manifest hermetically, ignoring the layered resolution |
| `--validate` | Parse and check a program, run nothing (exit 1 on error) |
| `--resolve` | Print the effective envelope at each run point, run nothing |
| `--verbose` | Log tool call inputs and outputs (useful for debugging) |
| `--dump-tools` | Write the connected MCP tool manifest to `mcp-tools.json` and exit |

The program itself is the positional argument (`nb flow.nb`) or stdin (`nb -`).

## The REPL

`nb` on a TTY with no input starts a live interpreter of the **same source syntax**:
each entered line is a directive, `run` invokes, Ctrl-D exits. It is the authoring and
debugging surface — the fastest way to build an envelope up line by line and watch what
it does before committing it to a file.

## Testing affordances

### Mock provider

Returns `"OK"` by default, or the value of the `Response` config key, or an inline
override — prefix a message with `MOCK:response=<text>`. No API key, no network, so
harness plumbing can be tested without spending tokens:

```bash
echo 'run MOCK:response=hi' | ./nb - --output jsonl
```

### Fake tools

nb reads `fake-tools.yaml` and treats those definitions as normal tools; when the
model calls one, nb returns the configured response. See `fake-tools.example.yaml` for
the format. Fake definitions **override** MCP definitions — by design, so you can fake
destructive actions or retune a tool description for alignment testing without
touching the real server.

Responses support macros, so each invocation produces fresh data instead of an
identical static string:

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

```yaml
response: '{"id": "{{$guid}}", "status": "{{$choice(pending,active,completed)}}", "created_at": "{{$timestamp}}"}'
```

### Built-in MCP server

`mcp-servers/mcp-tester/` is a self-contained C# MCP server with basic tools (echo,
reverse-echo, current-time) and markdown-driven prompts — useful for exercising the
MCP path without depending on a third-party server.

## Using nb as a library

Reference `nb.Core` (a self-contained `net10.0` assembly) and run programs in-process
— no subprocess, no stdout parsing, no `Environment.Exit`:

```csharp
using nb;
using nb.Transcript;

var result = await Nb.Program()
    .Provider("Anthropic")
    .Model("claude-sonnet-5")
    .System("You are a careful reviewer. Cite file:line.")
    .User("Review this diff:\n" + diffText)
    .Run()
    .RunAsync(config, new NbOptions { ProvidersDirectory = nbProvidersPath });

Console.WriteLine(result.Answer);   // plus Events, Usage, ExitReason, ExitCode, Warnings
```

Run outcomes (provider error, aborted turn, approval denial) come back on the result
rather than as exceptions; exceptions are reserved for things that stop a run from
happening at all. Full surface: [`docs/conversation-program-api.md`](docs/conversation-program-api.md).

## Providers

nb has no built-in providers — every provider is a plugin loaded at runtime from
`providers/` next to the binary, each in its own `AssemblyLoadContext`.

### Shipped providers

- **AzureOpenAI** — Chat Completions on classic Azure OpenAI resources
- **AzureFoundry** — Responses API on classic Azure OpenAI resources (needed for
  codex-family models like `gpt-5-codex`, and any other Responses-API-only model)
- **OpenAI** — direct OpenAI API
- **Anthropic** — Claude models with function calling
- **Google Gemini** — Google's generative AI models
- **LocalLlm** — local servers on the OpenAI wire
- **Mock** — testing provider, no API key

All are compiled into `bin/{Config}/net10.0/providers/` during build.

### Selecting a provider

A program selects with the `provider` and `model` directives, and can switch between
runs within one document. Connection (endpoint + key) stays in config; only the
non-secret model name travels in the program.

Provider entries are labels, not implementations. An entry's `Name` is free-form; the
optional `Provider` field names the implementation behind it, so several entries can
share one:

```jsonc
{ "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1", "Model": "qwen3-coder-next" },
{ "Name": "LocalAir",   "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8082/v1", "Model": "glm-4.5-air" }
```

Omit `Provider` and it defaults to `Name`.

**`EditToolStyle` is deprecated** (per entry). It selects the file-edit surface:
`EditReplace` (default) advertises `edit_file` + `write_file`; `ApplyPatch` advertises
`apply_patch` instead. They're mutually exclusive — GPT-family models confuse the two
when both are present.

It still works, and setting it prints a warning. Use the `harness codex` program
directive instead: `apply_patch` *is* the Codex edit surface, and the costume brings the
rest of that surface — the shell-only file access, the plan tool, `AGENTS.md`, the
environment block — rather than one field's worth of it. If you set `EditReplace`, just
remove the field; that's the default. A program that names a `harness` already overrides
this either way, advertising whatever that harness's target has.

### Which Azure provider do I want?

Match the API shape your deployment exposes:

| Your deployment URL looks like... | Use provider |
|---|---|
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/deployments/<name>/chat/completions?...` | `AzureOpenAI` |
| `https://<name>.{openai.azure.com,cognitiveservices.azure.com}/openai/responses?...` | `AzureFoundry` |

Both accept either the resource root or the full deployment URL in `Endpoint` — the
plugin strips to the host. The `Model` field is your **deployment name**, not the model
family name. If Azure shows an endpoint on `services.ai.azure.com` with a
`/api/projects/<project>/...` path, that's the newer Foundry Unified Endpoint and
neither provider targets it directly — open an issue if you need that variant.

### Writing your own provider

1. Create a project and add the abstractions package:
   ```bash
   dotnet add package nb.Providers.Abstractions
   ```
2. Implement `IChatClientProvider` — supply an `IChatClient` from
   Microsoft.Extensions.AI plus basic configuration wiring.
3. Build and copy the assembly to a new subdirectory under `providers/`.
4. Add the entry to `appsettings.json`.

See [nb.Providers.Abstractions](https://www.nuget.org/packages/nb.Providers.Abstractions)
for full documentation and examples.

## MCP configuration

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

`alwaysAllow` lists tools that skip approval; `["*"]` auto-approves everything from a
server. Remember that a program still has to *expose* the server with `mcp +name` —
configuring it isn't enough.

### HTTP servers and auth headers

Use `"type": "http"` with an `endpoint`. Auth goes in a `headers` object; values
support `${VAR}` interpolation against environment variables, so tokens stay out of
the committed `mcp.json`:

```json
"figma": {
  "type": "http",
  "endpoint": "https://mcp.figma.com/mcp",
  "headers": {
    "Authorization": "Bearer ${FIGMA_TOKEN}"
  }
}
```

Only values are interpolated (not keys). An unset variable logs a warning and resolves
to an empty string. Literal values work too.

## Theming

Interactive output (`--output interactive`, and the REPL) loads its color scheme from
`theme.json` at startup. Color names come from
[Spectre.Console](https://spectreconsole.net/appendix/colors). A high-contrast example
(WCAG AAA on the standard Windows console background, #0C0C0C):

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

## Building for distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Publishing `nb.csproj` alone works too, and gives a smaller artifact (no test harness,
no `mcp-tester`):

```bash
dotnet publish nb.csproj -c Release -r linux-x64 --self-contained -o /out
```

Either way, the provider plugins are built for the same RID and copied into
`providers/` next to the binary. If that copy ever comes up empty the build says so
(`No provider plugins found at …`) — the resulting binary can't run anything, so don't
ship past that warning.

**Configuration.** The publish output deliberately does **not** include your
`appsettings.json`; it holds live API keys, and shipping it would ship them. You get
`appsettings.example.json` instead. Point the deployed binary at a real config with
`--config <path>`, or drop an `appsettings.json` next to the executable — it is optional
at load, and nb starts without one (with no providers configured).

Ship `mcp.json` and `theme.json` alongside the executable for custom configurations.
Providers deploy to `providers/` next to the binary.

**Minimal containers.** Self-contained .NET aborts at startup without ICU
(`Couldn't find a valid ICU package installed on the system`), and language-toolchain
images like `golang` don't carry `libicu`. Either install it or set
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

## License

MIT License
