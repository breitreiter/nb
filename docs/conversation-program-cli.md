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

There is **no positional prompt**. `nb "some text"` is treated as a program *file*
named `some text` (almost always a "file not found" error). To run a one-off prompt,
make it a program: `echo 'run summarize this' | nb -`.

**Flags** — each varies how a program runs; none replaces or duplicates a program verb:

| Flag | Effect |
| --- | --- |
| `--output <mode>` | `jsonl` (default for a program), `porcelain`, or `interactive`. jsonl/porcelain put the transcript on stdout, chrome on stderr. |
| `--seed <file>` | Prepend a transcript (jsonl) as premise history before the program (§6). |
| `--config <file>` | Use exactly this config file (hermetic); otherwise config resolves in layers (§7). |
| `--mcp <file>` | Use this MCP manifest only; otherwise `mcp.json` resolves in layers. |
| `--validate` | Parse + semantically check the program, run nothing. Exit 1 on any error. |
| `--resolve` | Print the effective envelope at each run point, run nothing. Under a `harness` costume it adds a second line — `wire=` (the tool names the model is actually offered) and `dropped=` (canonical tools the costume discards) — because `tools=` echoes the directives, which under a costume are *requested*, not effective. |
| `--verbose` | Verbose engine diagnostics (to stderr). |
| `--dump-tools` | Write the MCP tool manifest to `mcp-tools.json` and exit. |

Approval, trust, no-tools, provider, model, and output-in-the-program are **program
concerns**, expressed as verbs (`approval`, `tools`, `provider`, `model`) or config —
not flags. That is the whole design: the program is the interface.

**I/O contract** (`--output jsonl`/`porcelain`): the transcript goes to **stdout**;
all chrome (tool logs, warnings, diagnostics) goes to **stderr**. Colour is disabled
when stdout is redirected or `NO_COLOR` is set. So `nb flow.nb 2>/dev/null` gives
clean, parseable stdout.
- `jsonl` — a typed event stream, one JSON object per line (§8).
- `porcelain` — plain text: `TOOL`/`RESULT` lines plus the answer verbatim (a final
  ```` ```json ```` fence survives byte-for-byte).

**Exit codes** (`$?`):

| Code | Meaning |
| --- | --- |
| `0` | `ok` — a final answer was produced. Also `oracle_miss` — the model asked the user for something the attached answer sheet does not cover; the run ended as it would have without an oracle, only the label differs (§4.1, *On `oracle`*). |
| `1` | Startup/config error (bad config, unparseable/invalid program, unassemblable engine, missing program/seed file) — emitted before any transcript. |
| `2` | `provider_error` — the provider/model failed mid-turn. |
| `3` | Aborted on a budget/limit — tool-call cap exhausted (`max_tool_calls`), a tool failed repeatedly (`tool_error_limit`), a token/wall-clock budget was spent (`token_budget` / `time_budget`), the provider throttled us past the retry budget (`rate_limited`), or the oracle's resolution budget ran out (`oracle_budget`). |
| `4` | `approval_denied` — a tool needed approval and policy denied it. |

The fine-grained reason also rides on the transcript's `result` trailer
(`exit_reason`). An unmatched tool call is **denied**, never prompted — grant what a run
needs with `approval` directives or config allow-lists. This does not depend on whether
stdin is a TTY: nb never asks for authorization mid-run, ever.
Authorization is something a program *states*, not something a run stops to collect.

**Throttling.** A provider rate-limit rejection is retried with exponential backoff
and jitter before it becomes an outcome, so a single 429 doesn't discard an agentic
run's accumulated work. Streaming calls are only retried before the first update
arrives — throttling happens at request admission, so this costs nothing in practice.
Tune per `ChatProviders` entry with `MaxRetries` (default 10; `0` disables retry),
`RetryMaxDelaySeconds` (a single backoff cap, default 60), and `RetryBudgetSeconds`
(the total time one request may spend retrying, default 300). Retrying stops at
whichever runs out first; the wall-clock budget is the one that matters, since a
gateway-wide limit lasts minutes and an attempt ladder alone gives up in seconds.

A throttle also **paces the requests that follow it** — retrying only the rejected call
lets the next turn charge straight back into the same limit, so a long run rediscovers
it turn after turn and pays for each rediscovery. The pace starts at a second, doubles
per throttle up to `RetryMaxDelaySeconds`, and halves back toward zero as calls succeed.

That adaptive pace is too gentle against a gateway with a fixed per-minute cap: two
clean responses put a run back at full speed, and a 150-turn run was throttled on 104
of them. `MinRequestIntervalMs` (default 0) is the blunt instrument for that case — a
floor the pace never decays below, held from the first call whether or not a throttle
has been seen. Size it as 60000 divided by the cap, times the number of runs you expect
in flight against the same gateway. It applies even with `MaxRetries: 0`.

A rejection is recognized as throttling from its status *or* its prose, including the
body of the HTTP response — some gateways signal wholesale capacity exhaustion as a
`402` whose message says nothing, with the only evidence in the response body. A `402`
that is genuinely about payment is not retried.

When the budget runs out the run ends as `rate_limited` rather than `provider_error`:
the distinction is the point — the same program re-run later may well succeed.

---

## 3. Source syntax

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
  the program file** (or cwd for a stdin program). Any other use of `@` is literal.

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

## 4. Directives

Three classes: **config** (set the envelope going forward, order matters), **turns**
(append messages), and **run** (invoke the model).

### 4.1 Config directives — the envelope

| Directive | Syntax | Meaning |
| --- | --- | --- |
| `provider` | `provider <name>` | Select the active provider (matched against `ChatProviders[].Name` in config) for subsequent runs. |
| `model` | `model <name>` | Select the model for subsequent runs. Overrides the active provider's model field in memory (both `Model` and `ChatDeploymentName`). |
| `harness` | `harness <name>` | Select the harness the run wears — its tool surface, result formatting and prompt preamble. Registered names: `nb`, `qwen-code`, `codex`, `claude-code`. An unknown one is a parse error, not a warning. **Every run wears one on purpose:** a program that names none inherits the active provider entry's `Harness` from config (else top-level `Harness`), and if config is silent too the run is refused before a model is called. `harness nb` asks for nb's own bare surface by name. A costume expects its own vendor's model — `claude-code` an Anthropic model, `codex` an OpenAI model, `qwen-code` a Qwen model; wedging another vendor's model into a costume is an experiment, not a test of that harness. |
| `oracle` | `oracle @<sheet.md>` | Attach an answer sheet: a scripted user that services the halt when a model ends its turn asking for information. After every run that ends `ok`, one small side call judges the model's last message against the sheet; on a confident hit the selected entries are appended verbatim as a `user` turn and the run continues. Anything else ends the run. See *On `oracle`* below and `plans/oracle-resolver.md`. |

**On `harness`.** It is a program directive rather than provider config because the
experiment worth running is *one model across two harnesses*, and that has to be
expressible as two files in a directory rather than as an edit to global config between
runs. A named harness brings its whole costume — prompt preamble, the project instruction
files its target reads (`AGENTS.md` under `codex`, `CLAUDE.md` under `claude-code`), and
that harness's environment block — see §4.5.
Runs that wear a non-default harness record it on the `result` trailer as `harness`
(omitted for the default). A costume also reports what it knowingly does not reproduce,
as run warnings, so a surprising result arrives with a suspect list attached. Further
costumes are planned; see `plans/harness-emulation.md`.

**On `oracle`.** nb has no user, so a model that ends its turn with *"which environment
should I deploy to?"* has halted on an unsatisfied dependency. An answer sheet is the
program's declaration of what that user would have said — a markdown file of headed
sections, each heading an entry id and its body the verbatim reply:

```markdown
## deploy-target
Staging only. Never touch prod during this exercise.

## customer-name
Acme Logistics.
```

**Authoring a sheet.** Key entries by **topic**, not by question — models phrase one
question ten ways. Write each body as the **full answer a real user would give**, with
enough detail to satisfy the question however the model chooses to ask it: the oracle
judges whether an entry *covers* the ask, and it is strict. Measured on a local model: a
subject asked *"please specify the cloud provider and platform"*, and a sheet whose
`deploy-target` entry said only *"Deploy to staging."* was judged `MISS` — defensibly,
since "staging" does not answer "AWS or Azure?". The entry above, which says what the
environment actually is, was a hit. A topic id names the entry; the body has to do the
work. The sheet holds what a user *knows* (facts, constraints, preferences), never how
the task should be solved: a sheet that carries the rubric turns question-asking into a
side channel to the answer key.

**The continuation rule: continue only on a confident hit; everything else ends the
run.** After a run ends `ok`, nb makes one small side call on the current provider. It is
shown the sheet and the model's last message and replies with entry ids, `DONE` or
`MISS`. The oracle *selects, never authors* — the reply the model then sees is composed
from the sheet bodies verbatim, so the transcript stays auditable.

| Verdict | Effect | `exit_reason` |
| --- | --- | --- |
| entries selected | their bodies join as one `user` turn; the run continues | (continues) |
| `MISS` — clearly asking, nothing on the sheet | run ends | `oracle_miss` (exit **0**) |
| `DONE` — not clearly waiting on the user | run ends | `ok` |

`oracle_miss` exits 0 on purpose: the run ended exactly as it would have without an
oracle, and only the label differs. It is the maintenance signal — the sheet needs an
entry, or the prompt produced a question nobody anticipated. The unanswered question is
the last `assistant_text` in the transcript; nothing papers over the ask. An oracle is
only consulted after `ok` — a run that ended on a budget, an error or a denial keeps that
reason.

Why the rule is shaped this way: *"is the model done, or asking?"* is hard in general,
because the ambiguous turns (*"Done — want me to also do X?"*) are most of the
population. Under this rule a false positive costs nothing (the run ends as it would
have) and a false negative needs the oracle to miss a *clear* question that *has* an
entry. The loop is bounded by `budget oracle_turns` (§4.4). There is no deflection and no
"I'm not sure" reply — those exist to handle the ambiguous middle, and the rule removes it.

On the wire, an oracle-supplied turn is an ordinary `user` event carrying two enrichment
fields, `source: "oracle"` and `keys: [...]` (the ids selected). Enrichment is ignored on
seed-load, so a resolved run replays from its own transcript as a plain conversation —
which is what makes it reproducible. The `result` trailer gains `oracle_turns` (omitted
when zero). Nothing is injected into the system prompt and no ask tool is advertised: a
steer toward one would perturb the prompt under test, no costume in this repo carries
one, and a model holding one still asks in prose anyway. The one prompt change is the
doom-loop nudge, which stops telling a model that nobody is home once a sheet is attached.

The directive carries the sheet's *resolved body*, not the path it came from:
`@answers.md` is expanded at parse time by the same whole-content include the turn
directives use (§4.5), and the text travels on the event, so a stored JSONL program stays
runnable after the file moves. (The sheet is not echoed into a captured transcript — the
oracle's *answers* are, as user turns, and those are what replay.) In practice the sheet
always arrives by `@file`: source syntax is line-oriented, so a multi-line sheet written
inline would parse its second line as a directive.

Output format is **not** a directive — it's the `--output` flag / caller's choice
(the program computes a conversation; delivery format is the caller's business).

Config directives apply to **every run after them until overridden**, so one document
can drive two models in sequence (see §9).

### 4.2 Tool-surface directives — `tools` and `mcp`

Delta semantics. Tokens are `+name`, `-name`, or `none` (reset/clear). `none` is only
meaningful first on the line — it clears the surface, and any `+name`/`-name` after it
apply to the cleared set.

- **`tools`** — native tools. Baseline **all-on**. Names: `bash`, `read_file`,
  `write_file`, `edit_file`, `find_files`, `grep`, `list_dir`, `apply_patch`,
  `fetch_url`, `search_web`, `todo`. `tools -bash` drops bash; `tools none` exposes none; `tools none
  +read_file` allows just that one. `todo` is a steering aid (a task-tracking tool
  plus a pending-todos nudge for models prone to abandoning work); `tools -todo`
  removes it, which also silences the nudge (no todos can be created without it).
  These names are **canonical under every `harness`** — a costume changes the names the
  *model* sees, not the names a program writes. Under `harness claude-code` the model is
  offered `Edit`, but the program still writes `tools -edit_file`, and the transcript
  records the wire name `Edit`. Writing a costume's wire name is an error, not a no-op,
  so the mismatch surfaces at `--validate` time rather than as a tool you thought you had
  removed.
- **`mcp`** — MCP servers. Baseline **strict-empty**: a program exposes no MCP tools
  unless it names servers. `mcp +figma` exposes that server's tools (as `figma_*`).
  MCP tools are exposed under the composite name `{server}_{tool}`. Naming a server
  that failed to start (crashed on startup, never completed the handshake) hard-fails
  the run (exit 1) — you asked for tools that will never arrive. A configured server
  that fails but is *not* named is a non-fatal warning instead, and the run continues.

A tool call outside the advertised surface is **refused** ("Error: Tool … not
found"), not executed. `--resolve` prints the resolved surface at each run point —
and under a costume also the **wire** surface, since a costume advertises a subset of
what a program names, under different names:

```console
$ nb --resolve rails-codex.nb
run 1: … harness=codex … tools=bash,edit_file,fetch_url,find_files,grep,list_dir,read_file,search_web,write_file …
       wire=shell_command,view_image dropped=edit_file,fetch_url,find_files,grep,list_dir,search_web,write_file
```

`dropped=` is the half worth reading before spending a run: nine tools named, two
offered, and no way to edit a file.

### 4.3 Approval directives — `approval`

`approval <key> <value>`. Layers onto the config-seeded approval policy for
subsequent runs.

| Key | Value | Effect |
| --- | --- | --- |
| `bash` | a command pattern | Auto-approve bash commands matching it (glob-ish). Matched against the **whole command string**, not the invocation inside it: `approval bash go *` allows `go mod tidy` but *not* `cd /work && go mod tidy`, since a rule matching anywhere in the line would be trivially escapable. Write the pattern against the line the model will actually send (`approval bash cd * && go *`), or allow the bare program and expect simple invocations. `*` **does** cross a newline, so `approval bash *` covers a heredoc or any other multi-line command; a trailing `*` already spanned `;` and `&&`, and a newline is another separator, not a new escape. |
| `mcp` | an allow glob | Auto-approve MCP tools matching it (matched against `{server}_{tool}`; `/` aliases `_`, so `weather/*` matches `weather_current`). |
| `search` | `allow` \| `prompt` | Auto-approve `search_web`. Needed by any run that means to search: an unapproved tool can never execute, so without this the search intent is recorded but the call reads as a denial. The `prompt` spelling is historical and means "not auto-approved" — nothing prompts. (`Approval.Search` in config does the same.) |
| `fetch` | `allow` \| `prompt` | Auto-approve `fetch_url`. Separate from `search` on purpose: reaching an arbitrary URL and running a web search are different grants, and allowing one should not silently confer the other. (`Approval.Fetch` in config does the same.) |
| `default` | `prompt` \| `deny` | Which auto-approve ladder an unmatched call runs. `prompt` (the default) tries explicit patterns, then the built-in safe-command list, then trust + sandbox; `deny` honours the explicit allow-list and nothing else. A call that survives either ladder unmatched is refused — the names are permissiveness tiers, not dispositions, and neither one asks. |
| `sandbox` | `none` \| `bwrap` \| `bwrap-net` | **Deprecated — scheduled for removal.** Run the bash child under a bubblewrap sandbox (Linux). `bwrap` = fs read-only, cwd + a fresh `/tmp` writable, secret dirs masked, no network; `bwrap-net` allows network. Requesting bwrap where it isn't available hard-fails (exit 1). A partial, Linux-only control that is weaker than the container it would sit inside; the directive will degrade to a warning rather than becoming a parse error. Don't build on it — run nb inside a container instead. |

`approval default deny` plus explicit `approval bash`/`approval mcp` allows = a run
that auto-approves exactly what it should and refuses everything else. (`Approval.Bash`
/ `Approval.McpTools` / `Approval.Default` / `Approval.Sandbox` in config do the same
outside a program.)

Under `default deny` the **explicit allow-list is the only thing that allows**. Outside
it, bash has two implicit grants that a first-time reader will not expect: a built-in
safe-command list (`ls`, `pwd`, `git status`, but also `make`, `npx`, `go build`,
`dotnet run` — build commands, i.e. arbitrary code), and trust, which auto-approves
any non-dangerous command whose path falls under the cwd. Both are suppressed by
`default deny`, so denial means denial; under `prompt` they still apply and the command
simply runs. **`default deny` is the primary mechanism** for a run that should honour
its allow-list and nothing else — not a footnote to the other two.

**None of this confines anything.** Approval decides what nb *records and refuses*, not
what the bash child can do: the child is an ordinary subprocess with no OS-level
isolation, so a model can read whatever the nb process user can read. The trust path
scoping is a convenience filter inherited from nb's coding-agent era, not a sandbox, and
no denylist could be one — block `cat` and `awk`, `od` or `python -c` still read files.
The useful output is the record: `tool_call.approved` plus the `denied` count in the
result trailer, which is what an eval reads to learn that the model reached outside its
surface.

If the workload is untrusted or adversarial, **run nb inside a container** — one
container, nb inside it, one filesystem. nb's file tools (`read_file`, `edit_file`,
`grep`, …) run in-process and never route through bash, so confining bash alone would
give the model two sets of paths for the same files. See
`plans/approval-is-not-a-boundary.md`.

### 4.4 Loop & budget directives — `loop` and `budget`

Run-level guards that layer onto config; they govern every run after them.

| Directive | Syntax | Effect |
| --- | --- | --- |
| `loop` | `loop <n>` \| `loop off` | Doom-loop detector. `loop <n>` sets the repetition threshold — after N repeated tool-call sequences a `<system_reminder>` nudge is injected and the run continues. `loop off` disables it. On by default (threshold 3 / config `DoomLoopThreshold`). Threshold must be ≥ 2. |
| `budget` | `budget tokens <n>` | Session-cumulative token ceiling. Once total usage crosses `<n>`, the run aborts with `exit_reason token_budget` (exit 3). Summed across all runs and tool-loop round-trips. Enforced against *estimated* counts when the provider reports none (§8) — it never silently stops enforcing. Default unlimited (config `TokenBudget`). |
| `budget` | `budget tool_calls <n>` | Per-turn tool-call cap for subsequent runs — overrides config `MaxToolCalls` and the trust-mode floor. Exhausting it ends the turn with `max_tool_calls`. |
| `budget` | `budget wall_ms <n>` | Session-cumulative wall-clock ceiling in milliseconds. Once elapsed time (from the first run) crosses `<n>`, the in-flight model call is **cancelled** and the run aborts with `exit_reason time_budget` (exit 3). This bounds a hung provider, not just a runaway loop. Default unlimited (config `WallClockBudgetMs`). |
| `budget` | `budget oracle_turns <n>` | How many times an `oracle` may resolve a halt and continue the run. When the model asks again with a hit after `<n>` resolutions, the run ends with `exit_reason oracle_budget` (exit 3). Default 8. |

The doom-loop nudge is a *soft* guard (it keeps the run going); `budget tokens` /
`budget wall_ms` are the *hard* ceilings for a runaway or hung model. All are purely
additive — a program that names none behaves exactly as before.

### 4.5 Turn directives — `system`, `user`, `assistant`

`system <text>`, `user <text>`, `assistant <text>` append one message of that role.
`system` is a plain message — **nb injects no persona a program did not ask for**. By
default a program gets exactly the `system` directives it writes; the only other way
persona arrives is a `harness` directive naming a costume, which brings that harness's
prompt preamble with it. `@file` and `\` continuation apply. Turns buffer and flush into
history at the next `run`.

Opting into a named harness opts into the whole costume — the preamble is not a separate
opt-in, because a program that asks to imitate another agent and is then told it should
*also* have requested the prompt has been failed by the tool. The bare default is
unchanged: name no harness and nothing is injected.

A costume also brings the **project instruction files its target reads** — `AGENTS.md`
for `codex`, `CLAUDE.md` for `claude-code` — collected from the repo root down to the working directory and wrapped as
that harness wraps them. This is the one thing a program does not fully determine: the
same program run in two directories sends different text, because that is exactly what
the harness being imitated does. Named-harness runs are therefore reproducible from the
transcript, not from the program alone.

It also brings that harness's **environment block** — working directory, shell or
platform, and the date, in the target's own wrapper. Same caveat, more strongly: the text
depends on where and *when* the program runs, so two runs of one program are never
byte-identical under a costume that sends one. `qwen-code` sends none; its omission list
says so.

#### Steering tool choice on the bare surface

"No persona" cuts both ways, and one consequence is worth stating because it is the only
intervention on tool selection this repo has ever measured working. A model asked to
modify files will often **rewrite them whole** rather than edit them, and on nb's bare
surface nothing tells it not to. One `system` sentence changes that:

```
system to modify an existing file use edit_file(path, old_string, new_string); use
system write_file only to create a new file; prefer editing over rewriting
```

Measured on one task, same model and fixture, that one directive moved `edit_file` calls
from **1 to 10**, `write_file` from 6 to 3, and the exit reason from `token_budget` to
`ok` — a task that previously aborted, finished.

Two honest caveats. It is **not a token saving**: input tokens moved under 4%, because
the dominant term is turns × accumulated context, not the shape of the writes. What
improved was work per token — the same spend produced roughly six times as much finished
work. And it is n=1 on one fixture; the effect is large and the mechanism is plain, but
it has not been replicated at scale. See
`bugs/Tool_Names_Diverge_From_Model_Native_Surface.md` for the full measurement history,
including a costume-based intervention that did *not* replicate.

A `harness` costume ships this steer in its preamble already (`qwen-code`'s says *"Prefer
`edit` over `write_file`"*), so this section is about the bare surface specifically.

Everything injected — preamble first, then the costume's furniture in the order that
harness uses, then the program's own `system` directives — is materialised into the
transcript as ordinary `system` messages,
so everything the model was sent is on the wire record and a `--seed` replay reproduces
it exactly, even if the costume or the project's instruction file has changed since.

### 4.6 `run` — the sole invocation

`run` sends the accumulated conversation to the model. `run <text>` is sugar for
`user <text>` then `run`. A program may have multiple `run`s; config directives
between them re-target the run. Token usage on the trailer **sums across all runs**.

### 4.7 `tool_call` / `tool_result` — JSONL only

These carry structured fields and **cannot** be written in source syntax. Author them
as JSONL to fabricate a **tool round as premise** — an assistant turn that called a
tool and the result it got — loaded into history like a seed, then a `run` continues.
They are recorded rounds you replay, not live invocations. Every `tool_call` must have
a matching `tool_result` (same `id`) before the run that consumes it, or exit 1.

---

## 5. Evaluation semantics

The evaluator walks the event stream in order: config directives update the forward
envelope (a `provider`/`model` change rebuilds the client — mid-stream model swap is
supported); turn directives buffer; `run` flushes buffered turns into history, folds
the tool surface, and invokes the model; at end of program, trailing buffered turns
still join the conversation.

Invariants:
- **No implicit persona.** Persona arrives only when the program asks for it — by
  `system`, or by `harness` naming a costume that carries a preamble. Name no harness
  and a program gets exactly the `system` directives it writes.
- **Completed rounds only.** Fabricated tool rounds must be well-formed (each call
  paired with a result, turns monotonic); malformed → exit 1.
- **Usage sums** across every run and tool-loop round-trip, and is estimated (and
  flagged) rather than dropped when a provider reports none — see §8.

---

## 6. Seeds

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

## 7. Configuration resolution

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

The harness resolves the same way, one level up from the program: a `harness` directive
wins, else the active provider entry's `"Harness"` in config, else top-level `"Harness"`.
Pair each entry with its vendor's costume (`"Harness": "claude-code"` on an Anthropic
entry, `codex` on an OpenAI one, `qwen-code` on a Qwen one) so a program that names only
a model still wears the right thing. A run that resolves no harness at all is refused —
see §10.

---

## 8. The JSONL wire format

`--output jsonl` emits one JSON object per line; `--seed` and JSONL programs read the
same. Field order is stable (type, turn, then type-specific). Every event has `"type"`
and `"turn"` (a monotonic per-round counter; `null` on run-level events).

**Core events** (round-trip losslessly — what a seed/JSONL program can author):

| `type` | Fields | Meaning |
| --- | --- | --- |
| `system` | `text` \| `content` | System-role message. |
| `user` | `text` \| `content` | User-role message. Enrichment: `source: "oracle"` + `keys[]` when an answer sheet supplied the turn (§4.1). |
| `assistant_text` | `text` \| `content` | Assistant prose. |
| `tool_call` | `id`, `name`, `arguments` (JSON obj, types preserved), `approved`?, `approval_reason`? | A tool invocation. `id` is the join key. `approved` is `allow`/`deny`; `approval_reason` names the ladder rung that decided it (`pre-approved`, `safe`, `trust`, `default-deny`, `no-match`). A **denial** appends the near miss in parentheses — which rungs were consulted, and for each whether it was *skipped* (switched off elsewhere, e.g. `Trust=false`) or *refused* (evaluated and said no, with the cause). The rung stays the leading token, so filtering on `no-match` by prefix keeps working. |
| `tool_result` | `id`, `output` (exact model-facing string), `result`? | The result for the matching `id`. `output` round-trips byte-for-byte. |
| `run` | `prompt`? | Invocation directive. On output, a past run appears as the `assistant_text` it produced. |
| `provider` / `model` | `name` | Config directive. |
| `harness` | `name` | Harness-selection directive (§4.1). Registered: `nb`, `qwen-code`, `codex`, `claude-code`. |
| `mcp` / `tools` | `reset`?, `add`[], `remove`[] | Tool-surface delta. |
| `approval` | `key`, `value` | Approval-policy directive. |
| `loop` | `enabled`, `threshold`? | Doom-loop directive. `threshold` present only when `enabled`. |
| `budget` | `key`, `value` | Resource-budget directive (`tokens` \| `tool_calls` \| `wall_ms` \| `oracle_turns`). |
| `oracle` | `sheet` | Answer sheet body (resolved, not a path). See §4.1. |

**Enrichment events** (emitted on output, **ignored on seed-load**): `thinking`
(`text`), `assistant_json` (`value`), and the `approved`/`approval_reason`/`result` fields.

**The `result` trailer** (one per run, `turn: null`):

```json
{"type":"result","turn":null,"exit_reason":"ok","usage":{"input":10,"output":5,"total":15},"turns":1,"tool_calls":0,"provider":"Mock"}
```

Fields: `exit_reason` (§2), `usage{input,output,total,estimated?}`, `turns`,
`tool_calls`, `duration_ms`?, `provider`, `harness`?, `oracle_turns`? (how many halts an
answer sheet serviced; omitted when zero). `harness` names the costume the run
wore and is **omitted for nb's own** — so a default run's trailer is unchanged.
`provider` names the entry that actually answered and is **always emitted**, unlike
`harness`: it is the field a corpus is attributed by, and omitting it when it matches the
configured default would leave a reader unable to resolve it, since the default is
config-dependent. Read `exit_reason` for the outcome; read the last `assistant_text` for
the answer.

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

## 9. Worked examples

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

**Service a model's questions from an answer sheet:**
```
tools none
oracle @answers.md
system You are a deployment assistant. If the target environment is not stated, ask for it and stop.
run Please deploy the api service.
```
with `answers.md`:
```markdown
## deploy-target
The target environment is the staging Kubernetes cluster on AWS (EKS, eu-west-1).
Never touch production during this exercise.
```
The model asks, the oracle selects `deploy-target`, the body enters as a `user` turn
(`"source":"oracle","keys":["deploy-target"]`), and the model finishes:
```bash
nb deploy.nb 2>/dev/null | jq -c 'if .type=="user" and .source=="oracle" then .keys elif .type=="result" then [.exit_reason, .oracle_turns] else empty end'
```
A `MISS` ends the run `oracle_miss`; read the last `assistant_text` to see what was
asked, then add the entry.

**Inspect before running:**
```bash
nb --resolve flow.nb    # print provider/model/output/surface/approval per run
nb --validate flow.nb   # semantic check; exit 1 on any error
```

---

## 10. Failure modes to expect (the sharp edges)

- **`nb "some text"`** → treated as a program *file* named "some text" → file-not-found,
  exit 1. There is no bare-prompt mode. Wrap it: `echo 'run some text' | nb -`.
- **Unknown verb / missing value / bad delta token** → parse error, exit 1. (Note
  `output` is no longer a verb — an `output` line is an unknown directive.)
- **Unknown provider name** → caught by `--validate` (exit 1).
- **No harness named** — not in the program, not on the provider entry, not top-level in
  config → the first `run` is refused, exit 1, before any request goes out: *"no harness
  named. Every run wears one …"*. The bare surface is not a fallback; write `harness nb`
  if that is what you mean. `--resolve` prints `harness=(none …)` for the same case.
- **A `provider`/`model` directive whose client cannot be built** → the run **aborts**,
  exit 1. This covers an unknown entry, an entry naming an implementation that did not
  load, missing required keys (an expired `ApiKey`, an env var absent from a CI job), and
  a throwing `CreateClient`. A program naming a provider is asserting a dependency, so
  nb refuses rather than answering from whichever client was live before it — that
  substitution used to exit 0 with a transcript indistinguishable from a working run.
  Note `--validate` catches only unknown *names*: an entry that is present but
  unbuildable validates clean and fails at run time.
- **Malformed fabricated round** (unpaired call/result, non-monotonic turns) → exit 1.
- **Tool call outside the advertised surface** → refused as "not found" (native tools
  are all-on; MCP is strict-empty until `mcp +server`).
- **Unmatched approval** → denied (**never hangs**, in any mode); the model gets a
  structured denial it can route around, and the turn still completes (exit 0) unless
  policy forces otherwise. A run whose terminal failure *was* the denial exits `4`
  (`approval_denied`).
- **`budget tokens` overshoot:** the ceiling is checked after each model round-trip, so
  a run can exceed it by up to one round-trip before aborting (`token_budget`, exit 3) —
  it can't preempt a generation in flight. `budget wall_ms`, by contrast, cancels the
  in-flight model call at the deadline (`time_budget`, exit 3), so it *does* bound a hung
  provider — but a tool already executing (bash/MCP) still runs to its own per-op timeout
  before the run stops. `loop`/`budget` values below their floor (threshold < 2,
  non-positive) are rejected by `--validate` (exit 1).
- **`oracle` verdicts on a reasoning model.** The side call gives the model ~4k output
  tokens because a thinking model spends its reasoning inside the cap: at 200 tokens a
  local GLM returned an empty verdict every time. A verdict cut off before any text
  arrives is reported as a warning and treated as `DONE`, so the run ends `ok` rather
  than continuing on a guess — if you see that warning, the model needs more room, not a
  different sheet. Expect a verdict to cost a few hundred output tokens on such models;
  `budget tokens` bounds it like everything else. And a sheet entry that names the topic
  but does not actually answer the question is a `MISS`, not a hit (§4.1, *Authoring a
  sheet*).
- **`approval sandbox bwrap` on a non-Linux / no-bwrap host** → hard-fail, exit 1.
- **`mcp +server` naming a server that failed to start** → hard-fail, exit 1 (the
  program selected tools that will never arrive). A configured server that fails but
  is never named is a non-fatal warning to stderr and the run continues.
- **`mcp +server` naming a server that isn't in the manifest at all** → hard-fail,
  exit 1, same reason. The message says "is not configured in mcp.json" to separate
  it from the failed-to-start case.
- **`--mcp` pointing at a missing or malformed manifest** → hard-fail, exit 1. An
  explicit manifest is strict, matching `--config`; the layered `mcp.json` lookup
  stays lenient, where an absent layer is normal.
- **Bash quoting:** both the sandboxed and unsandboxed bash paths pass the command to
  bash verbatim, so bash's own quoting rules apply — `$VAR` and `$(...)` expand in
  double quotes and not in single quotes, exactly as in a terminal. (Before 2026-08-12
  the unsandboxed path escaped `$` and backticks, which stopped expansion but also
  corrupted them inside single quotes, breaking `awk '{print $1}'` and `grep 'foo$'`.)
