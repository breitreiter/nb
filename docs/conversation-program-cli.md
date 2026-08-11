# nb Conversation-Program Reference — CLI

**Status:** stable reference (v1). Describes current behavior on the
`conversation-program` branch. Versioned contract — when the format changes, this
document changes with it.

**Audience:** an autonomous agent (e.g. Claude) driving the `nb` **command-line
tool** as a subprocess — authoring programs, invoking nb, and parsing its output.
Written to be loaded into context and acted on directly.

> **Using nb in-process as a .NET library instead?** See
> `conversation-program-api.md` for `Nb.RunAsync`, the program builder, and the
> typed result. This doc is the CLI/subprocess surface.

> **If you have pre-de-soup assumptions, these are the breaking changes:**
> nb no longer has a **bare-prompt mode** (`nb "text"` is a program *filename* now —
> pipe `echo 'run text' | nb -`). The program is the **positional argument**, so
> `--program` is gone. Removed flags: `--program`, `--spec`, `--system`, `--approve`
> (use the `approval bash` directive), `--nobash` (use `tools none`), `--trust`
> (use `"Trust": true` in config). The **`output` program verb is gone** — output
> format is the `--output` flag only. Kept flags: `--output`, `--seed`, `--config`,
> `--mcp`, `--validate`, `--resolve`, `--verbose`, `--dump-tools`.

---

## 1. What nb is

nb is a **conversation-program evaluator**. A conversation-program is one ordered
document carrying everything a run needs — provider, model, tool surface, approval
policy, fabricated history, and the live prompt — so *one document is one runnable
program*. nb is **not** a chat client: there is no bare-prompt mode. It runs a
program and, when you want continuity, you pass it explicitly (capture a transcript,
feed it back as a seed). It is stateless between invocations — no history file.

---

## 2. Invoking nb

```
nb [options] [program-file | -]
```

**Input** — nb always runs a program:
- `nb flow.nb` — run the program in that file.
- `nb -`, or piped stdin with no positional (`… | nb`) — run the program from stdin.
- `nb` on a TTY with no input — the **program REPL** (§3): interpret the source
  syntax line by line.

There is **no positional prompt**. `nb "some text"` is treated as a program *file*
named `some text` (almost always a "file not found" error). To run a one-off prompt,
make it a program: `echo 'run summarize this' | nb -`.

**Flags** — each varies how a program runs; none replaces or duplicates a program verb:

| Flag | Effect |
| --- | --- |
| `--output <mode>` | `jsonl` (default for a program), `porcelain`, or `interactive`. jsonl/porcelain put the transcript on stdout, chrome on stderr. |
| `--seed <file>` | Prepend a transcript (jsonl) as premise history before the program (§8). |
| `--config <file>` | Use exactly this config file (hermetic); otherwise config resolves in layers (§9). |
| `--mcp <file>` | Use this MCP manifest only; otherwise `mcp.json` resolves in layers. |
| `--validate` | Parse + semantically check the program, run nothing. Exit 1 on any error. |
| `--resolve` | Print the effective envelope at each run point, run nothing. |
| `--verbose` | Verbose engine diagnostics (to stderr). |
| `--dump-tools` | Write the MCP tool manifest to `mcp-tools.json` and exit. |

Approval, trust, no-tools, provider, model, and output-in-the-program are **program
concerns**, expressed as verbs (`approval`, `tools`, `provider`, `model`) or config —
not flags. That is the whole design: the program is the interface.

**I/O contract** (`--output jsonl`/`porcelain`): the transcript goes to **stdout**;
all chrome (tool logs, warnings, diagnostics) goes to **stderr**. Colour is disabled
when stdout is redirected or `NO_COLOR` is set. So `nb flow.nb 2>/dev/null` gives
clean, parseable stdout.
- `jsonl` — a typed event stream, one JSON object per line (§10).
- `porcelain` — plain text: `TOOL`/`RESULT` lines plus the answer verbatim (a final
  ```` ```json ```` fence survives byte-for-byte).

**Exit codes** (`$?`):

| Code | Meaning |
| --- | --- |
| `0` | `ok` — a final answer was produced. |
| `1` | Startup/config error (bad config, unparseable/invalid program, unassemblable engine, missing program/seed file) — emitted before any transcript. |
| `2` | `provider_error` — the provider/model failed mid-turn. |
| `3` | Aborted on a budget/limit — tool-call cap exhausted (`max_tool_calls`), a tool failed repeatedly (`tool_error_limit`), or a token/wall-clock budget was spent (`token_budget` / `time_budget`). |
| `4` | `approval_denied` — a tool needed approval and policy denied it. |

The fine-grained reason also rides on the transcript's `result` trailer
(`exit_reason`). Runs are headless (no TTY on the program path), so an unmatched tool
call is **denied** rather than prompted — grant what a run needs with `approval`
directives or config allow-lists.

---

## 3. The program REPL

With no input on a TTY, `nb` starts a REPL that **interprets the same source syntax**
(§4–§5) line by line — it is not a chat client. Each entered line is a program
directive: `provider Mock`, `system be terse`, `user hi`, `run` (or `run <text>`)
invokes the model and renders the reply live. Config directives set the envelope
going forward; turns buffer until the next `run`; a parse error prints and continues.
There are no slash-commands and no persona. Ctrl-D (EOF) exits, exactly as a source
program ends. It doubles as the authoring/debugging surface for programs.

---

## 4. Source syntax

One rule: **a logical line is `<verb> <content>`** — the first whitespace-delimited
token is the verb, everything after the first space is the content (trimmed).
Verb/content collisions resolve by position: `system system design is hard` →
verb `system`, content `system design is hard`.

- **Continuation:** a physical line whose trimmed end is a lone `\` continues onto
  the next line, joined with `\n`.
- **Comments / blank lines:** a line whose first non-space char is `#` is skipped
  (this also covers a leading `#!` shebang). Blank lines are skipped.
- **`@file` include:** if a directive's *entire* content is `@<path>` (no
  whitespace), it's replaced by that file's contents. Paths resolve **relative to
  the program file** (or cwd for a stdin/REPL program). Any other use of `@` is literal.

```
#!/usr/bin/env nb
provider Anthropic
model claude-sonnet-5
system You are a careful reviewer. \
Cite file:line for every claim.
user @./diff.txt
run
```

Syntactic errors (unknown verb, missing value, malformed delta token) are parse
errors → exit 1. Semantic errors (unknown provider) are caught by `--validate`.

---

## 5. Directives

Three classes: **config** (set the envelope going forward, order matters), **turns**
(append messages), and **run** (invoke the model).

### 5.1 Config directives — the envelope

| Directive | Syntax | Meaning |
| --- | --- | --- |
| `provider` | `provider <name>` | Select the active provider (matched against `ChatProviders[].Name` in config) for subsequent runs. |
| `model` | `model <name>` | Select the model for subsequent runs. Overrides the active provider's model field in memory (both `Model` and `ChatDeploymentName`). |

Output format is **not** a directive — it's the `--output` flag / caller's choice
(the program computes a conversation; delivery format is the caller's business).

Config directives apply to **every run after them until overridden**, so one document
can drive two models in sequence (see §11).

### 5.2 Tool-surface directives — `tools` and `mcp`

Delta semantics. Tokens are `+name`, `-name`, or the lone `none` (reset/clear).

- **`tools`** — native tools. Baseline **all-on**. Names: `bash`, `read_file`,
  `write_file`, `edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`,
  `fetch_url`, `todo`. `tools -bash` drops bash; `tools none` exposes none; `tools none
  +read_file` allows just that one. `todo` is a steering aid (a task-tracking tool
  plus a pending-todos nudge for models prone to abandoning work); `tools -todo`
  removes it, which also silences the nudge (no todos can be created without it).
- **`mcp`** — MCP servers. Baseline **strict-empty**: a program exposes no MCP tools
  unless it names servers. `mcp +figma` exposes that server's tools (as `figma_*`).
  MCP tools are exposed under the composite name `{server}_{tool}`. Naming a server
  that failed to start (crashed on startup, never completed the handshake) hard-fails
  the run (exit 1) — you asked for tools that will never arrive. A configured server
  that fails but is *not* named is a non-fatal warning instead, and the run continues.

A tool call outside the advertised surface is **refused** ("Error: Tool … not
found"), not executed. `--resolve` prints the resolved surface at each run point.

### 5.3 Approval directives — `approval`

`approval <key> <value>`. Layers onto the config-seeded approval policy for
subsequent runs.

| Key | Value | Effect |
| --- | --- | --- |
| `bash` | a command pattern | Auto-approve bash commands matching it (glob-ish). |
| `mcp` | an allow glob | Auto-approve MCP tools matching it (matched against `{server}_{tool}`; `/` aliases `_`, so `weather/*` matches `weather_current`). |
| `default` | `prompt` \| `deny` | What an unmatched call does — prompt (default) or refuse outright. |
| `sandbox` | `none` \| `bwrap` \| `bwrap-net` | Run the bash child under a bubblewrap sandbox (Linux). `bwrap` = fs read-only, cwd + a fresh `/tmp` writable, secret dirs masked, no network; `bwrap-net` allows network. Requesting bwrap where it isn't available hard-fails (exit 1). |

`approval default deny` plus explicit `approval bash`/`approval mcp` allows = a run
that auto-approves exactly what it should and refuses everything else. (`Approval.Bash`
/ `Approval.McpTools` / `Approval.Default` / `Approval.Sandbox` in config do the same
outside a program.)

### 5.4 Loop & budget directives — `loop` and `budget`

Run-level guards that layer onto config; they govern every run after them.

| Directive | Syntax | Effect |
| --- | --- | --- |
| `loop` | `loop <n>` \| `loop off` | Doom-loop detector. `loop <n>` sets the repetition threshold — after N repeated tool-call sequences a `<system_reminder>` nudge is injected and the run continues. `loop off` disables it. On by default (threshold 3 / config `DoomLoopThreshold`). Threshold must be ≥ 2. |
| `budget` | `budget tokens <n>` | Session-cumulative token ceiling. Once total usage crosses `<n>`, the run aborts with `exit_reason token_budget` (exit 3). Summed across all runs and tool-loop round-trips. Enforced against *estimated* counts when the provider reports none (§9) — it never silently stops enforcing. Default unlimited (config `TokenBudget`). |
| `budget` | `budget tool_calls <n>` | Per-turn tool-call cap for subsequent runs — overrides config `MaxToolCalls` and the trust-mode floor. Exhausting it ends the turn with `max_tool_calls`. |
| `budget` | `budget wall_ms <n>` | Session-cumulative wall-clock ceiling in milliseconds. Once elapsed time (from the first run) crosses `<n>`, the in-flight model call is **cancelled** and the run aborts with `exit_reason time_budget` (exit 3). This bounds a hung provider, not just a runaway loop. Default unlimited (config `WallClockBudgetMs`). |

The doom-loop nudge is a *soft* guard (it keeps the run going); `budget tokens` /
`budget wall_ms` are the *hard* ceilings for a runaway or hung model. All are purely
additive — a program that names none behaves exactly as before.

### 5.5 Turn directives — `system`, `user`, `assistant`

`system <text>`, `user <text>`, `assistant <text>` append one message of that role.
`system` is a plain message — nb injects no persona; a program gets only the `system`
directives it writes. `@file` and `\` continuation apply. Turns buffer and flush into
history at the next `run`.

### 5.6 `run` — the sole invocation

`run` sends the accumulated conversation to the model. `run <text>` is sugar for
`user <text>` then `run`. A program may have multiple `run`s; config directives
between them re-target the run. Token usage on the trailer **sums across all runs**.

### 5.7 `tool_call` / `tool_result` — JSONL only

These carry structured fields and **cannot** be written in source syntax. Author them
as JSONL to fabricate a **tool round as premise** — an assistant turn that called a
tool and the result it got — loaded into history like a seed, then a `run` continues.
They are recorded rounds you replay, not live invocations. Every `tool_call` must have
a matching `tool_result` (same `id`) before the run that consumes it, or exit 1.

---

## 6. Evaluation semantics

The evaluator walks the event stream in order: config directives update the forward
envelope (a `provider`/`model` change rebuilds the client — mid-stream model swap is
supported); turn directives buffer; `run` flushes buffered turns into history, folds
the tool surface, and invokes the model; at end of program, trailing buffered turns
still join the conversation.

Invariants:
- **No implicit persona.** A program gets exactly the `system` directives it writes.
- **Completed rounds only.** Fabricated tool rounds must be well-formed (each call
  paired with a result, turns monotonic); malformed → exit 1.
- **Usage sums** across every run and tool-loop round-trip, and is estimated (and
  flagged) rather than dropped when a provider reports none — see §9.

---

## 7. Seeds

`--seed <file>` prepends a transcript (jsonl events) as premise history before the
program body. A seed's own `system` messages survive (they append as premise). The
seed must contain **completed rounds** — every `tool_call` paired with its
`tool_result`, turns monotonic — or the load fails (exit 1). This is how you carry
continuity across stateless invocations:

```bash
echo 'run start a haiku about autumn' | nb - --output jsonl > turn1.jsonl
echo 'run now finish it' | nb - --seed turn1.jsonl
```

---

## 8. Configuration resolution

Config resolves in layers, later winning: install defaults (`appsettings.json` next
to the binary) → user (`~/.config/nb/config.json`, honoring `XDG_CONFIG_HOME`) →
nearest project `.nb/config.json` (walking up from cwd) → `NB_`-prefixed environment
variables. Friendly env aliases: `NB_PROVIDER`, `NB_MODEL`, `NB_OUTPUT` (plus raw
`NB_ChatProviders__0__ApiKey`-style paths). `--config <file>` collapses the file
layers to one file (hermetic); env still applies. `mcp.json` resolves in the same
install → user → project layers, merged by server name; `--mcp <file>` selects one
manifest hermetically.

Provider connection (endpoint + key) lives in config, never in a program. Only the
non-secret **model name** travels in a program. Provider and MCP-server *names* are
installation-local; `--validate` catches an unknown name before a run.

---

## 9. The JSONL wire format

`--output jsonl` emits one JSON object per line; `--seed` and JSONL programs read the
same. Field order is stable (type, turn, then type-specific). Every event has `"type"`
and `"turn"` (a monotonic per-round counter; `null` on run-level events).

**Core events** (round-trip losslessly — what a seed/JSONL program can author):

| `type` | Fields | Meaning |
| --- | --- | --- |
| `system` | `text` \| `content` | System-role message. |
| `user` | `text` \| `content` | User-role message. |
| `assistant_text` | `text` \| `content` | Assistant prose. |
| `tool_call` | `id`, `name`, `arguments` (JSON obj, types preserved), `approved`? | A tool invocation. `id` is the join key. |
| `tool_result` | `id`, `output` (exact model-facing string), `result`? | The result for the matching `id`. `output` round-trips byte-for-byte. |
| `run` | `prompt`? | Invocation directive. On output, a past run appears as the `assistant_text` it produced. |
| `provider` / `model` | `name` | Config directive. |
| `mcp` / `tools` | `reset`?, `add`[], `remove`[] | Tool-surface delta. |
| `approval` | `key`, `value` | Approval-policy directive. |
| `loop` | `enabled`, `threshold`? | Doom-loop directive. `threshold` present only when `enabled`. |
| `budget` | `key`, `value` | Resource-budget directive (`tokens` \| `tool_calls` \| `wall_ms`). |

**Enrichment events** (emitted on output, **ignored on seed-load**): `thinking`
(`text`), `assistant_json` (`value`), and the `approved`/`result` fields.

**The `result` trailer** (one per run, `turn: null`):

```json
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":10,"output":5,"total":15},"turns":1,"tool_calls":0}
```

Fields: `exit_reason` (§2), `usage{input,output,total,estimated?}`, `turns`,
`tool_calls`, `duration_ms`?. Read `exit_reason` for the outcome; read the last
`assistant_text` for the answer.

**Estimated usage.** `usage` normally carries the provider's own counts. Two
degradations are handled rather than papered over:

- A provider that reports the parts but no total (Anthropic has no `total_tokens`
  field; some gateways drop it) gets its `total` derived as `input + output`. Still a
  measurement — no flag.
- A provider that reports *nothing* — commonly a proxy, router, or gateway that
  terminates the stream and drops the final usage chunk, or a server that ignores
  `stream_options.include_usage` — gets counts estimated from message size (history +
  tool schemas in, response out, ~3.5 chars/token). The trailer then carries
  `"estimated": true` and a warning goes to stderr.

An estimated trailer is a guardrail, not billing data: it's off by roughly ±30% and
blind to provider-side overheads. `"estimated"` is omitted entirely when the counts
are measured, so a normal trailer is unchanged. Multipart `content` (multimodal) is an array of parts
(`{"kind":"text",…}` / `{"kind":"image",…,"note":…}`); images don't fully round-trip
in v1 — the durable stand-in is `note`.

---

## 10. Worked examples

**Run a one-off prompt and extract the answer:**
```bash
echo 'run what is 2+2?' | nb - --output jsonl 2>/dev/null \
  | jq -rs 'map(select(.type=="assistant_text"))[-1].text'
```

**A program file with a fabricated tool round (JSONL), then a run:**
```jsonl
{"type":"system","turn":0,"text":"You summarize command output."}
{"type":"user","turn":1,"text":"what's in this dir?"}
{"type":"assistant_text","turn":2,"text":"Let me list it."}
{"type":"tool_call","turn":2,"id":"c1","name":"bash","arguments":{"command":"ls"}}
{"type":"tool_result","turn":2,"id":"c1","output":"foo.txt bar.txt"}
{"type":"run","turn":3}
```
`nb round.jsonl` — the round enters history as premise; the run continues from there.

**Mid-stream model swap (cheap draft, careful critique), one document:**
```
model claude-haiku-4-5
run draft a first pass at the summary
model claude-sonnet-5
run now critique and tighten the draft
```

**Deterministic headless with a tight approval policy:**
```
approval default deny
approval bash "git status"
approval bash "git diff*"
run review the staged changes and summarize them
```

**Inspect before running:**
```bash
nb --resolve flow.nb    # print provider/model/output/surface/approval per run
nb --validate flow.nb   # semantic check; exit 1 on any error
```

---

## 11. Failure modes to expect (the sharp edges)

- **`nb "some text"`** → treated as a program *file* named "some text" → file-not-found,
  exit 1. There is no bare-prompt mode. Wrap it: `echo 'run some text' | nb -`.
- **Unknown verb / missing value / bad delta token** → parse error, exit 1. (Note
  `output` is no longer a verb — an `output` line is an unknown directive.)
- **Unknown provider name** → caught by `--validate` (exit 1); at run time a bad
  `provider`/`model` directive warns and keeps the current client. Prefer `--validate`.
- **Malformed fabricated round** (unpaired call/result, non-monotonic turns) → exit 1.
- **Tool call outside the advertised surface** → refused as "not found" (native tools
  are all-on; MCP is strict-empty until `mcp +server`).
- **Headless + unmatched approval** → denied (never hangs); the model gets a
  structured denial it can route around, and the turn still completes (exit 0) unless
  policy forces otherwise.
- **`budget tokens` overshoot:** the ceiling is checked after each model round-trip, so
  a run can exceed it by up to one round-trip before aborting (`token_budget`, exit 3) —
  it can't preempt a generation in flight. `budget wall_ms`, by contrast, cancels the
  in-flight model call at the deadline (`time_budget`, exit 3), so it *does* bound a hung
  provider — but a tool already executing (bash/MCP) still runs to its own per-op timeout
  before the run stops. `loop`/`budget` values below their floor (threshold < 2,
  non-positive) are rejected by `--validate` (exit 1).
- **`approval sandbox bwrap` on a non-Linux / no-bwrap host** → hard-fail, exit 1.
- **`mcp +server` naming a server that failed to start** → hard-fail, exit 1 (the
  program selected tools that will never arrive). A configured server that fails but
  is never named is a non-fatal warning to stderr and the run continues.
- **Unsandboxed bash quoting:** the non-sandboxed bash tool escapes `$` and backticks,
  so `$(...)` / `$VAR` don't run there; the bwrap sandbox passes the command raw.
