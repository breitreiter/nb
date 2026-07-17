# nb Conversation-Program Reference — Library API (`nb.Core`)

**Status:** stable reference (v1). Describes current behavior on the
`conversation-program` branch. Versioned contract — when the API changes, this
document changes with it.

**Audience:** an autonomous agent (e.g. Claude) or developer using nb **in-process
as a .NET library** — referencing `nb.Core`, building a program in C#, calling
`Nb.RunAsync`, and reading a typed result. Written to be loaded into context and
acted on directly.

> **Driving the `nb` command-line tool as a subprocess instead?** See
> `conversation-program-cli.md` for flags, the source-syntax grammar, and the
> JSONL text format. This doc assumes you build programs from typed objects
> in-process and never touch stdout parsing or source syntax.

> **If you have pre-de-soup assumptions:** output format is no longer part of a
> program — `NbProgramBuilder.Output(...)`, `RunResult.OutputMode`, and the
> `OutputEvent` record are removed. Everything else (`Nb.RunAsync`, the builder,
> `NbOptions`, `RunResult`, the event types) is unchanged.

---

## 1. What nb is (as a library)

nb is a **stateless evaluator of conversation-programs**. A program is an ordered
list of events carrying everything a run needs — provider, model, tool surface,
approval policy, fabricated history, and the live prompt. `nb.Core` exposes this
as a library: build a program, call `Nb.RunAsync(config, program, options)`, get
back a typed `RunResult`. No process is spawned, no `Environment.Exit` is called,
and nothing is written to your stdout — the whole engine runs in your process.

Reference the `nb.Core` assembly (a self-contained `net10.0` library; not on
NuGet — reference the built assembly or the project). Types live in `nb` (the
facade) and `nb.Transcript` (the event schema).

```csharp
using nb;             // Nb, NbProgramBuilder, NbOptions, RunResult, NbStartupException
using nb.Transcript;  // TranscriptEvent and its subtypes, UsageInfo
```

---

## 2. The entry point

```csharp
public static class Nb
{
    // Primary: run a program (a list of events) and return a typed result.
    public static Task<RunResult> RunAsync(
        IConfiguration config,
        IReadOnlyList<TranscriptEvent> program,
        NbOptions? options = null,
        CancellationToken cancellationToken = default);

    // Sugar: start a fluent builder.
    public static NbProgramBuilder Program();
}
```

- `config` — any `Microsoft.Extensions.Configuration.IConfiguration` you build:
  the active provider, its endpoint/key/model, MCP config, etc. (§5). Connection
  secrets live here, never in the program.
- `program` — the ordered event list (build it with the builder, §3, or construct
  `TranscriptEvent`s directly, §4).
- `options` — per-invocation knobs (§6). Optional.

**Throws** on failure to assemble or a malformed program (§7); run *outcomes* come
back on the result, not as exceptions.

---

## 3. Building a program — `NbProgramBuilder`

`Nb.Program()` returns a fluent builder. Each method appends one directive;
`RunAsync` (or `Build()`) yields the program.

```csharp
var result = await Nb.Program()
    .Provider("Anthropic")            // config directives (envelope, order matters)
    .Model("claude-sonnet-5")
    .System("You are a careful reviewer. Cite file:line.")
    .User("Review this diff:\n" + diffText)
    .Run()                            // invoke the model on the accumulated state
    .RunAsync(config, new NbOptions { ProvidersDirectory = nbProvidersPath });
```

Builder methods (each returns the builder):

| Method | Directive | Meaning |
| --- | --- | --- |
| `.Provider(name)` | provider | Select the active provider (matched to `ChatProviders[].Name`) for subsequent runs. |
| `.Model(name)` | model | Select the model for subsequent runs (mid-stream swap is supported — call again before another `Run`). |
| `.System(text)` | system | Append a system-role message (a plain message — **not** a special "the prompt"). |
| `.User(text)` | user | Append a user-role message. |
| `.Assistant(text)` | assistant | Append an assistant-role message (premise). |
| `.Run(prompt?)` | run | Invoke the model. `Run("x")` = `User("x")` then `Run()`. Multiple runs allowed; usage sums across them. |
| `.Add(events)` | (any) | Append pre-built `TranscriptEvent`s — for directives the builder has no shortcut for (tool-surface, approval, fabricated tool rounds) and for seeding (§4). |

The builder deliberately covers the common directives. For **tool-surface**
(`mcp`/`tools`), **approval**, and **fabricated tool rounds**, construct the
events and `.Add()` them (§4).

Config directives set the envelope for **every run after them**; turn directives
buffer and flush at the next `Run`. **No implicit persona** is injected — a
program gets exactly the `System` messages you add (none if you add none). That's
the correct default for a harness.

---

## 4. The event model — `TranscriptEvent`

Every program is ultimately an `IReadOnlyList<TranscriptEvent>`, and
`RunResult.Events` is the completed conversation as the same typed records. Build
events directly when you need directives the builder doesn't expose, or when you
have a captured `Events` list to replay as premise. All live in `nb.Transcript`;
every event has `int? Turn` (a monotonic per-round counter — set it when
fabricating multi-event rounds; null for run-level events).

**Message events** (`MessageEvent` base: `string? Text` or
`IReadOnlyList<ContentPart>? Content`):
- `SystemEvent`, `UserEvent`, `AssistantTextEvent` — `{ Turn, Text }`.

**Tool round** (author these to replay a past tool exchange as premise):
- `ToolCallEvent` — `{ Turn, string Id, string Name, JsonObject? Arguments, string? Approved }`.
- `ToolResultEvent` — `{ Turn, string Id, string Output }`. `Output` is the exact
  model-facing string. Every call needs a matching result (same `Id`) in the same
  program before a `Run` consumes it, or the program is rejected (§7).

**Invocation:**
- `RunEvent` — `{ Turn, string? Prompt }`. `Prompt` is the inline-user sugar.

**Config directives** (run-level, `Turn` null):
- `ProviderEvent { Name }`, `ModelEvent { Name }`.
- `McpEvent` / `ToolsEvent` (both `SurfaceDirectiveEvent`): `{ IReadOnlyList<string> Add, IReadOnlyList<string> Remove, bool Reset }`.
  - `tools` baseline is all-on: `new ToolsEvent { Remove = ["bash"] }`, or
    `new ToolsEvent { Reset = true }` to clear. Native names: `bash`, `read_file`,
    `write_file`, `edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`, `fetch_url`.
  - `mcp` baseline is **strict-empty** for a program: `new McpEvent { Add = ["figma"] }`
    exposes that server's tools (advertised as `figma_*`).
- `ApprovalEvent { string Key, string Value }` — `Key` ∈ `bash` (auto-approve a
  command pattern), `mcp` (an allow glob vs `{server}_{tool}`), `default`
  (`prompt`|`deny`), `sandbox` (`none`|`bwrap`|`bwrap-net`).

**Output-only enrichment** (present in `RunResult.Events`, ignored if you replay
them as input): `ThinkingEvent`, `AssistantJsonEvent`, `ResultEvent` (the run
trailer — see `RunResult` instead), and the `Approved`/`Result` fields.

Example — a fabricated tool round plus tool-surface control, via `.Add()`:
```csharp
var program = Nb.Program()
    .System("You summarize command output.")
    .Add(new TranscriptEvent[]
    {
        new UserEvent        { Turn = 1, Text = "what's here?" },
        new AssistantTextEvent { Turn = 2, Text = "Let me list it." },
        new ToolCallEvent    { Turn = 2, Id = "c1", Name = "bash",
                               Arguments = new JsonObject { ["command"] = "ls" } },
        new ToolResultEvent  { Turn = 2, Id = "c1", Output = "foo.txt bar.txt" },
    })
    .Add(new[] { new ToolsEvent { Remove = new[] { "write_file", "edit_file" } } })
    .Run("now summarize what you found")
    .Build();
var result = await Nb.RunAsync(config, program, options);
```

---

## 5. Configuration

You supply the `IConfiguration`. The engine reads the active provider and its
connection from a `ChatProviders` array; the program only carries the non-secret
model name.

```csharp
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ActiveProvider"]         = "Anthropic",
        ["ChatProviders:0:Name"]   = "Anthropic",
        ["ChatProviders:0:ApiKey"] = apiKey,
        ["ChatProviders:0:Model"]  = "claude-sonnet-5",
        // optional: ["MaxToolCalls"], ["MaxContextTokens"], per-provider Temperature, etc.
        // provider plugin location for a library host (see §6):
        ["ProvidersPath"]          = nbProvidersPath,
    })
    .Build();
```

Provider/MCP-server *names* are installation-local (they must match what your
config defines). A `provider`/`model` directive naming something unconfigured
warns and keeps the current client rather than switching.

---

## 6. Per-invocation options — `NbOptions`

```csharp
new NbOptions
{
    ProvidersDirectory = "/path/to/nb/providers", // REQUIRED for a library host (see below)
    Cwd                = Directory.GetCurrentDirectory(), // shell/native-tool working dir
    Trust              = false,   // auto-approve non-dangerous tools within the cwd sandbox
    NoBash             = false,   // expose no native tools (MCP-only)
    Verbose            = false,
    McpManifestPath    = null,    // explicit MCP manifest; null = layered mcp.json
    ApprovePatterns    = new[] { "git status" }, // bash auto-approve patterns
    DiagnosticsWriter  = null,    // where engine chrome goes; null suppresses it
}
```

**`ProvidersDirectory` — the one you must set as a library host.** nb's AI
providers (Anthropic, AzureOpenAI, …) are separate plugin DLLs loaded at runtime
from a `providers/` directory. In your process `AppContext.BaseDirectory` is *your*
output dir, which has no `providers/` — so point `ProvidersDirectory` (or
`config["ProvidersPath"]`) at where nb's providers are deployed (e.g. an nb
install's `bin/.../providers`). If no provider can be loaded/created, `RunAsync`
throws `NbStartupException`. A provider that fails to load emits a diagnostic to
`DiagnosticsWriter`.

**Approval, headless-style.** In-process there's no TTY, so an unmatched tool call
is denied (not prompted). Grant what a run needs via `ApprovePatterns`, `Trust`,
or `ApprovalEvent`s in the program; `ApprovalEvent{Key="default",Value="deny"}`
plus explicit allows is the deterministic posture.

---

## 7. The result, outcomes, and exceptions

```csharp
public sealed record RunResult
{
    public IReadOnlyList<TranscriptEvent> Events { get; init; } // the completed conversation
    public string  Answer     { get; init; }   // last non-empty assistant text
    public UsageInfo? Usage   { get; init; }    // { Input, Output, Total }, summed across runs
    public string  ExitReason { get; init; }    // "ok" | "provider_error" | "max_tool_calls" | ...
    public int     ExitCode   { get; init; }    // 0 | 2 | 3 | 4
    public IReadOnlyList<string> Warnings { get; init; } // non-fatal evaluator warnings
}
```

**Run outcomes are on the result, never thrown.** A provider failure, an aborted
turn, or an approval denial come back as `ExitReason`/`ExitCode`:

| `ExitReason` | `ExitCode` | Meaning |
| --- | --- | --- |
| `ok` | 0 | A final answer was produced. |
| `provider_error` | 2 | The provider/model failed mid-turn. |
| `max_tool_calls` / `tool_error_limit` | 3 | Turn aborted (tool-call budget / repeated tool failure). |
| `approval_denied` | 4 | A tool needed approval and policy denied it. |

**Exceptions are for things that stop a run from happening at all:**
- `NbStartupException` — the engine couldn't be assembled: no usable chat client
  (bad/missing provider, no `ProvidersDirectory`), an invalid `Approval.Sandbox`,
  or `bwrap` requested where unavailable.
- `TranscriptFormatException` — the program is malformed (an unpaired/ill-ordered
  fabricated tool round).

```csharp
try
{
    var r = await Nb.RunAsync(config, program, options);
    if (r.ExitReason != "ok") { /* inspect r.ExitReason, r.Warnings */ }
    Use(r.Answer, r.Events, r.Usage);
}
catch (NbStartupException e)      { /* config/provider/environment problem */ }
catch (TranscriptFormatException e) { /* the program you built is malformed */ }
```

---

## 8. Notes and sharp edges

- **No implicit persona.** A program gets only the `System` messages you add.
  "Missing system prompt" is a choice, not a bug.
- **Tool calls outside the advertised surface are refused** ("not found"), not
  executed. `tools` is all-on by default; `mcp` is strict-empty until you add a
  server.
- **Usage sums** across every run and tool-loop round-trip in the program.
- **Chrome suppression is global for the call.** `RunAsync` redirects the engine's
  `AnsiConsole` to `DiagnosticsWriter` (or discards it) for the duration and
  restores it after — so it isn't safe to run many `Nb.RunAsync` calls truly
  concurrently in one process today (they'd race on that global). Sequential calls
  are fine.
- **Bash quoting.** The unsandboxed bash tool escapes `$` and backticks, so command
  substitution / `$VAR` expansion don't run there; a bwrap sandbox
  (`ApprovalEvent{Key="sandbox",Value="bwrap"}`, Linux) passes the command raw.
- **Cancellation.** `RunAsync` takes a `CancellationToken`; pass one for long runs.
