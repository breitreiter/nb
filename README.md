# NotaBene (nb)

**A small-program evaluator for LLM automation.** Evals, model comparison, prompt
regression, and tool-use testing, driven from one file you can check into git.

nb runs a **conversation-program**: one ordered directive document carrying the
provider, model, tool surface, approval policy, fabricated history, and the live
prompt. One document is one runnable program. Every invocation is stateless, output
is machine-readable by default, and the output schema is the same schema the input
accepts, so record, edit, replay is the normal workflow.

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

A run is what its document says: the history it fabricates, the persona it writes,
the tools it declares. Nothing is injected and nothing carries over from the last
invocation, so a scripted unit of LLM work is cheap to run a thousand times and easy to
inspect afterwards. A program can also put the run in another agent's costume with
`harness`, to measure a model through the surface it was trained against.

## Documentation

| Page | Covers |
|---|---|
| [`docs/conversation-program-cli.md`](docs/conversation-program-cli.md) | The full reference: source syntax, every directive, evaluation semantics, seeds, the JSONL wire format, failure modes. |
| [`docs/conversation-program-api.md`](docs/conversation-program-api.md) | Running programs in-process from C# via `nb.Core`. |
| [`docs/providers.md`](docs/providers.md) | Shipped providers, selecting one, gateway routing, the Azure variants, writing your own. |
| [`docs/mcp.md`](docs/mcp.md) | Configuring MCP servers, auth headers, the built-in test server. |
| [`docs/testing.md`](docs/testing.md) | The Mock provider, fake tools, the eval suite, the oracle bench. |
| [`docs/distribution.md`](docs/distribution.md) | Publishing self-contained binaries, the container image, and what ships in them. |

## What it's for

- **Evals.** A program per case, `--output jsonl` into your scorer. No history file, so
  cases run in parallel without interfering. nb's own eval suite (`evals/run.sh`) is
  written this way against the Mock provider.
- **Model comparison.** Same program, `provider` or `model` swapped, or swapped
  mid-document so one run hands off from a cheap model to an expensive one.
- **Prompt regression.** The program is a text file. Diff it, review it, bisect it.
- **Tool-use and alignment testing.** Declare a tool surface (`tools`, `mcp`),
  fabricate a prior tool round the model believes it already made, or define
  `fake-tools.yaml` entries so destructive tools return canned results.
- **Harness comparison.** Run one model through `harness codex` and `harness
  claude-code` and diff the transcripts. `harness` is a program directive, so the
  experiment is two files in a directory rather than a config edit between runs.
- **A subroutine inside a bigger agent.** Call `Nb.RunAsync` in-process and get a typed
  result, or spawn `nb` as a subprocess and parse stdout.

The shell and file tools are a tool surface you hand the model under test, shaped per
program by `tools` and `mcp` and governed by a declarative approval policy. That policy
decides what is recorded and refused. It is not a security boundary: the bash child has
no OS-level isolation, and nb's file tools run in-process. For an untrusted or
adversarial workload, run nb inside a container. The reference explains the reasoning
under *Approval directives*.

## Prerequisites

- .NET 10 SDK to build from source, or the .NET 10 runtime for pre-built binaries.
- An API key for at least one supported provider: Azure OpenAI, OpenAI, Anthropic,
  Google Gemini, or any local server on the OpenAI wire. The Mock provider needs no key
  and is enough to develop programs against.
- **Windows only:** [Git for Windows](https://git-scm.com/download/win). nb uses Git
  Bash for its shell tool on Windows. PowerShell is not supported, because models mix
  bash and PowerShell idioms when given a tool named `bash` and produce broken commands.
  If `bash.exe` is not found at install time, nb says where to get it.

## Installation

### Build from source (recommended)

```bash
git clone https://github.com/breitreiter/nb
cd nb
cp appsettings.example.json appsettings.json   # then add your provider config
dotnet build
cd bin/Debug/net10.0
echo 'run MOCK:response=hello' | ./nb -
```

nb must run from the bin directory, where the provider DLLs live.

### Pre-built binaries

Binaries are in the [releases section](https://github.com/breitreiter/nb/releases).
They are not code-signed, so expect a security warning. On Windows, SmartScreen: click
"More info", then "Run anyway" ([docs](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/)).
On macOS, Gatekeeper: see Apple's guide on [safely opening apps](https://support.apple.com/en-us/102445).

## Configuration

Configuration holds connection details (endpoints, keys) and defaults. Everything about
a particular run, which model, which tools, what is allowed, belongs in the program.

1. **Providers.** Edit `appsettings.json` with keys and endpoints. Configure as many as
   you like; a program picks one by name. If a model has a non-standard context window,
   set `MaxContextTokens` on its entry. Details in [`docs/providers.md`](docs/providers.md).
2. **MCP servers** (optional). Copy `mcp.example.json` to `mcp.json`. Details in
   [`docs/mcp.md`](docs/mcp.md).
3. **Theme** (optional). Colors for interactive output in `theme.json`; see the
   reference, §2.

### Config resolution

Config resolves in layers, later winning: install defaults (`appsettings.json` next to
the binary), then user config (`~/.config/nb/config.json`, honoring `XDG_CONFIG_HOME`),
then the nearest project `.nb/config.json` walking up from the current directory, then
`NB_`-prefixed environment variables (`NB_ActiveProvider`, `NB_ChatProviders__0__ApiKey`).
This keeps API keys out of the install directory and lets a CI job set provider and
model without editing shared config. `--config <file>` uses a single file and ignores
the layers, which is what a reproducible test run wants.

Aliases for the common knobs: `NB_PROVIDER`, `NB_MODEL`, `NB_OUTPUT`. `mcp.json`
resolves in the same install, user (`~/.config/nb/mcp.json`), project (`.nb/mcp.json`)
layers, merging server definitions by name; `--mcp <file>` selects a single manifest.

## Running a program

```bash
nb flow.nb                           # run a program file
echo 'run summarize this' | nb -     # a one-off program on stdin
```

With no program, `nb` prints its help and exits 2. There is no interactive mode.

The positional argument is a program file, so `nb "some text"` looks for a file named
"some text". To run a one-off prompt, wrap it in a `run`: `echo 'run some text' | nb -`.

**Stateless, with explicit continuity.** nb reads and writes no history file, so
parallel runs do not interfere. Carry continuity with `--seed`, which prepends a captured
transcript as premise:

```bash
echo 'run start a haiku about autumn' | nb - --output jsonl > turn1.jsonl
echo 'run now finish it'              | nb - --seed turn1.jsonl
```

### Output

A program defaults to `--output jsonl`. Both machine modes put the transcript on stdout
and all chrome (tool logs, warnings) on stderr, so a script captures a clean result:

```bash
nb flow.nb                       # jsonl: a typed event stream plus a result trailer
nb flow.nb --output porcelain    # plain text: TOOL/RESULT lines and the answer verbatim
nb flow.nb 2>/dev/null | jq -r 'select(.type=="assistant_text").text'
```

Color is disabled when stdout is redirected or `NO_COLOR` is set. Exit codes: `0`
success, `2` provider error, `3` turn aborted on a budget, limit, or repeated failures,
`4` approval denied. The fine-grained reason (`token_budget`, `oracle_miss`, and so on)
is on the transcript's `result` trailer as `exit_reason`.

The program format and the transcript format are the same schema: `--output jsonl`
emits it and `--seed` loads it.

### Inspecting a program without running it

```bash
nb --validate flow.nb    # parse and check; exit 1 on error
nb --resolve  flow.nb    # print the effective envelope at each run point
```

`--validate` is cheap enough to run over a whole eval corpus in CI before spending
tokens on it.

## The program format

Each line is `<verb> <content>`:

- **Config directives** (`provider`, `model`, `harness`, `oracle`, `mcp`, `tools`,
  `approval`, `loop`, `budget`) set the envelope going forward.
- **Turn directives** (`system`, `user`, `assistant`) append messages.
- **`run`** invokes the model on the accumulated state. `run <text>` is shorthand for a
  `user` turn followed by `run`.

A trailing `\` continues content onto the next line, `#` lines are comments, and
`@file` as a directive's whole content includes that file, resolved relative to the
program. Config directives can appear between runs, so one document can drive two
models:

```
model haiku
run quick triage of this log
model opus
run now analyze the root cause
```

A program is never given a default persona. It gets the `system` directives it writes
and nothing else, which is what an eval wants. The one exception is explicit: a
`harness` directive brings its costume's prompt with it.

The directives in brief, each covered in full in the
[reference](docs/conversation-program-cli.md):

- **`tools` and `mcp`** reshape the tool surface with delta tokens (`tools -bash`,
  `tools none`, `mcp +figma`). Native tools are all on by default; MCP servers are
  exposed only when named.
- **`harness`** puts the run in another agent's costume: its tool names and schemas,
  its prompt preamble, its project instruction files, its environment block. The
  registered names are `nb`, `qwen-code`, `codex`, and `claude-code`, and every run must
  resolve one, from the program or from the provider entry's `Harness` field.
- **`loop` and `budget`** are run-level ceilings: a doom-loop nudge, and hard limits on
  tokens, tool calls, wall-clock, and oracle turns.
- **`oracle @sheet.md`** attaches an answer sheet, a scripted user that answers when
  the model stops and asks a question. `oracle provider <entry>` picks the judge model.
- **`approval`** is a declarative allow-list. An unmatched tool call is denied and
  recorded, never asked about. Any run that means to search needs
  `approval search allow`.

## Using nb as a library

Reference `nb.Core`, a self-contained `net10.0` assembly, and run programs in-process
with no subprocess, no stdout parsing, and no `Environment.Exit`:

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
rather than as exceptions. Exceptions are reserved for things that stop a run from
happening at all. Full surface: [`docs/conversation-program-api.md`](docs/conversation-program-api.md).

## License

MIT. See [LICENSE.txt](LICENSE.txt).
