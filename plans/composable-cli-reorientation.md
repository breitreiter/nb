---
kind: plan
title: Composable-CLI reorientation — nb as an automation tool first
created: 2026-07-07
updated: 2026-07-11
status: current
state: active
touches:
  files:
    - Program.cs
    - ConversationManager.cs
    - ConfigurationService.cs
    - KitManager.cs
    - HistoryLock.cs
    - MCP/McpManager.cs
    - Shell/BashTool.cs
  features: [headless, scripting, run-specs, config, seeds, statelessness, approval, sandbox]
provenance:
  author: claude
---

# Composable-CLI reorientation — nb as an automation tool first

> **Revision (2026-07-12): kits removed entirely.** This plan (Pillar 1, "What
> this costs") kept kits as demoted prompt/tool *overlays* composing on top of a
> spec. That's superseded: kits are deleted outright (`KitManager`, `+kit`
> tokens, `/kit`, `.nb_active_kits.json`, `kits.json`). The tool-focusing use
> case they served is deprioritized along with nb-as-general-CLI-client, and the
> `mcp`/`tools` directives (evaluator plan) become the sole tool-surface
> mechanism. Interim: MCP tools are unexposed until those directives land. The
> text below is preserved as the original intent.

## Why this plan exists

nb was born a chat app, spent time as a coding agent, and is settling into
its real niche: a **turbo-powered `claude -p`** — a scriptable, composable
unit of LLM work. The weaver eval harness is the first serious consumer to
use it that way, and the experience report is unambiguous: every layer of nb
that made sense for the earlier identities is now something a programmatic
caller has to fight.

What weaver had to do to run `nb --trust "<prompt>"`:

- wrap the whole process in an **external bwrap jail** because nb has no
  filesystem boundary of its own (`bugs/shell-tool-no-filesystem-sandbox.md`);
- **strip ANSI and brace-scan** for the answer because the renderer destroys
  the model's ```` ```json ```` fences (`plans/headless-machine-output.md`);
- switch models by **SSH-ing to the model host**, because provider/model are
  frozen in a global `appsettings.json` next to the binary — which also puts
  plaintext API keys inside the jail mount;
- set `HOME` to a temp dir to dodge per-cwd history/kit state;
- **ignore nb's exit code**, because it's 0 on everything short of a config
  crash;
- accept that any tool needing approval would have **hung or crashed** on the
  redirected console (`Console.ReadKey` with no TTY).

The claude leg of the same harness is one flag and eight clean output lines.

This plan is the umbrella reorientation: what nb should look like when the
**invocation** — not the installation, not the directory — is the unit of
configuration, and when the primary consumer is a script, a cron job, a CI
step, or another agent. We accept that some changes make nb *worse* as an
interactive chat/coding agent; interactive mode becomes the authoring and
debugging surface for automation, not the product.

**One explicit anti-goal, decided up front:** the answer is *not* "add more
command switches." Making every config value mutable via ever-longer command
lines just relocates the problem — every caller re-assembles the same six
decisions by hand, and the flag surface grows without bound. The design
center is **declarative run specs + layered resolution**; flags shrink to
one-off overrides of last resort.

## Where nb actually is today (verified)

Everything below is confirmed against source, not remembered.

**Singular / global / implicit — the cramped parts:**

- **One global config in the install dir.** `appsettings.json` loads from
  `AppContext.BaseDirectory`, `optional: false` (`ConfigurationService.cs:37-45`).
  No `.AddEnvironmentVariables()` — API keys come only from JSON sitting next
  to the binary. `mcp.json` (`MCP/McpManager.cs:277-296`) and `kits.json`
  (`KitManager.cs:17-19`) load from the same place.
- **Provider/model are not selectable per invocation.** `ActiveProvider` is a
  config string; the only runtime switch is the interactive `/provider`
  command (`CommandProcessor.cs:66-105`). There is no `--provider`, `--model`,
  `--config`, `--output-format`, or `--continue/--fresh` flag
  (`Program.cs:212-258`).
- **One assembled system prompt per process** — base + shell env + provider
  layer + model-slug layer + NB.md, built once at startup
  (`Program.cs:385-408`). Kits inject a separate system message but only
  bundle prompt + MCP servers (`KitManager.cs:7`).
- **MCP is reachable only through kits.** With kits configured, an MCP
  server's tools exist for the model *only* while a kit referencing that
  server is active (`ConversationManager.cs:183-186`); with no active kit,
  zero MCP tools. So automating an MCP tool means front-loading `+kit`
  tokens on every invocation — and because activation persists to
  `.nb_active_kits.json`, each run mutates per-directory state that leaks
  into the next run unless it remembers `--no-kits`. Tool-clutter gating
  (a good interactive idea) became the only access path (bad for
  automation).
- **History is implicit per-cwd state, always continued.**
  `.nb_conversation_history.json` auto-loads and auto-appends on every run;
  there is no stateless mode. The single-writer `HistoryLock` makes a second
  concurrent run in the same dir **silently stateless** (`Program.cs:421-501`)
  — the worst possible behavior for parallel automation: not an error, just
  different semantics.
- **One interactive render path.** All output flows through global
  `AnsiConsole`; fences become Spectre `Rule`s
  (`Utilities/MarkdownRenderer.cs:76-86`); no `IsOutputRedirected` check
  anywhere; answer and chrome share stdout; exit code is 0 for everything
  except startup failures.
- **Approval is a TTY conversation.** Seven separate `Console.ReadKey` loops
  in `ConversationManager.cs`; the only non-interactive escapes are `--trust`
  (blanket) and `--approve` (bash-only globs; the TODO to extend it to MCP is
  still open). On a redirected console these prompts hang or throw — weaver
  hit this in the field.
- **No OS sandbox.** The trust boundary is a C# string heuristic;
  `echo $(cat /etc/passwd)` auto-approves without `--trust`
  (`bugs/shell-tool-no-filesystem-sandbox.md`).
- **No telemetry.** No token counts, no cost, no duration in any output;
  weaver's only cost proxy is externally-measured wall time.

**Already automation-friendly — build on, don't rebuild:**

- Single-shot mode, piped stdin, `--system`, `--approve`, `--trust`,
  `--nobash`, `+kit` tokens, `--dump-tools` (already goes to stderr).
- The provider plugin layer (`IChatClientProvider` → `IChatClient`) is thin,
  clean, and headless-neutral.
- Tool *execution* is pure (approval/rendering live in ConversationManager,
  not the tools); TrustSandbox, CommandClassifier, ApprovalPatterns,
  FileReadTracker, DoomLoopDetector, ToolErrorTracker are all TTY-free.
- MockProvider + fake tools + `evals/run.sh` already treat single-shot as the
  test surface (`plans/Testing.md`: "single-shot mode + `--approve` makes
  integration testing trivial").
- The rule in `rules/model-policy-in-prompt-layers.md` (policy in prompt
  layers, zero model branches in the engine) is fully compatible with this
  plan and constrains it: profiles select *which* prompt layers apply; they
  don't move policy into code.

## The design: four pillars

### Pillar 1 — Run specs: the unit-of-work document is the interface

An earlier draft called these "profiles" — named bundles pre-registered in
layered config. That's registry-first thinking, and the registry is the
wrong primary: calling apps and orchestrating models need to create and
rewrite these bundles as cheaply as they create a prompt — generate, run,
discard. A registry would recreate the original sin one level up (mutate
shared config to customize a run). So the primary artifact is a
**document**, not a config entry. A **run spec** is a small declarative
file describing the contextual envelope for one unit of work; an invocation
is "do this task, inside this envelope." Specs sit closer to scripts than
to configuration, and that's the intent.

```jsonc
// eval-runner.nb.json — self-contained, written by a human or generated by a caller
{
  "Extends": "headless",               // inherit a built-in or named spec
  "Provider": "LocalLlm",
  "Model": "qwen-coder",
  "Prompt": { "Base": "prompts/eval.md", "ShellEnv": true, "ProjectContext": false },
  "Tools": { "Native": ["bash", "read_file", "grep", "list_dir"], "McpServers": [], "Todo": false },
  "Approval": { "Default": "deny", "Bash": ["weaver *"], "Sandbox": "bwrap" },
  "Output": "jsonl",                    // interactive | porcelain | jsonl
  "Seed": null,                         // optional transcript to load as premise (Pillar 3)
  "Limits": { "MaxToolCalls": 80, "BashTimeoutSeconds": 300 }
}
```

```bash
nb --spec ./eval-runner.nb.json "<prompt>"   # a file the caller just wrote
nb --spec eval-runner "<prompt>"             # by name, resolved through config layers
NB_SPEC=eval-runner nb "<prompt>"            # env, for CI
```

Design decisions:

- **Document first, registry second.** One schema regardless of how the
  spec arrives: a file path, a name resolved through the config layers
  (Pillar 2), or a built-in (`headless`, `chat`). A "profile" is just a
  spec somebody saved and named. A calling app never mutates shared config
  to customize a run — it writes a temp file and points at it. Generate →
  run → discard is the intended lifecycle, hermetic and parallel-safe by
  construction.
- **`Extends` is the authoring cheat code.** A spec names one parent and
  overrides fields, so most machine-written specs are 3–5 lines on top of
  a built-in. Single inheritance, no mixins — stacking is what kits are
  for.
- **Specs can carry the work itself.** An optional `Task` field holds the
  prompt (with `{arg}` placeholders filled from positional args), making a
  spec file a complete runnable job — `nb --spec nightly-triage.json` —
  the natural unit for cron, CI steps, and agent-to-agent delegation.
  Without `Task`, the spec is a reusable envelope and the prompt comes
  from args/stdin as today.
- **Machine-writability has acceptance criteria, not vibes.** A published
  JSON schema; `nb --spec X --validate` (parse, resolve, report errors,
  run nothing); `nb --spec X --resolve` printing the fully merged
  effective spec — the read-modify-write loop a model needs to learn the
  format from a live example. Validation errors name the field and the
  allowed values, so a model that writes a bad spec can fix it from the
  error alone.
- **Exactly one spec per invocation.** Specs don't stack; kits do. A spec
  may pin default kits.
- **Kits survive as overlays — and lose their MCP monopoly.** A spec's
  `Tools.McpServers` exposes servers directly; automation never touches a
  kit to reach a tool. A kit stays what it is today (prompt fragment + MCP
  server gate) and composes *on top of* the active spec via `+kit`, where
  gating returns to being what it was meant to be: an interactive
  convenience for keeping the tool list focused, not the access path.
  `.nb_active_kits.json` goes away with the rest of the hidden state
  (Pillar 3): kit activation is per-invocation, declared in the spec or
  via `+kit` tokens, and persists nothing.
- **Every field defaults sensibly**, so a spec can be three lines: the
  empty spec reproduces baseline behavior, and built-in `headless`/`chat`
  specs ship in the box so `nb --spec headless "..."` works with zero
  authoring.
- **Specs select prompt layers; they don't contain policy.** The
  provider/model-slug prompt layering (`Program.cs:391-403`) keeps working
  underneath; a spec can override the *base* file (what `--system` does
  today) and toggle the assembled sections. This keeps
  `rules/model-policy-in-prompt-layers.md` intact.

### Pillar 2 — Layered config resolution (git-style)

Today nb has exactly one config layer and it's in the wrong place (next to
the binary — which is why a Debug build path is load-bearing in weaver, and
why plaintext keys ended up inside a jail mount). Replace with standard
resolution, later wins:

1. **Install defaults** — shipped `appsettings.json`, providers, prompts.
2. **User config** — `~/.config/nb/` (`config.json`, `specs/`, `mcp.json`,
   `kits.json`, `secrets`). API keys live here or in env, never in the
   install dir. Named specs resolve from here.
3. **Project config** — `.nb/config.json` found by upward walk from cwd
   (same walk NB.md already does, `Program.cs:103-146`). Can set the default
   spec for a directory tree, define project-local specs/kits/MCP servers.
4. **Environment** — `NB_SPEC`, `NB_PROVIDER`, `NB_MODEL`, `NB_OUTPUT`,
   plus key material (`NB_ANTHROPIC_API_KEY`, ...). Via
   `.AddEnvironmentVariables("NB_")` — this alone unlocks most CI scenarios.
5. **Flags** — deliberately few: `--spec`, `--output`, `--seed`,
   `--config <path>` (point at an explicit config file for hermetic runs),
   plus the existing behavioral flags. New knobs default to config-only;
   a flag has to earn its place as a genuinely per-invocation concern.

MCP servers and kits become *definitions* that merge across layers; specs
*reference* them by name. `mcp.json`/`kits.json` remain as files but are read
from each layer, not just the install dir.

**Names are installation-local — decided trade (2026-07-07).** Provider
names, MCP server names, and named `Extends` parents are user-supplied
config keys, so a spec referencing them is portable only under naming
discipline — exactly like a shell script calling `jq` or `ssh myserver`.
We accept this rather than invent machinery (a canonical-name registry is
package management; capability-based resolution is a dependency solver;
inlining server definitions trades local *names* for local *paths*). Two
mitigations, both already planned:

1. `--validate` resolves every name at parse time and enumerates what the
   installation actually defines (`unknown MCP server 'weather'; this
   installation defines: wx, fetch, test-runner`) — nonportability fails
   fast with a model-fixable error, never mid-run.
2. The portable unit is the **project, not the spec file**: a repo ships
   `.nb/config.json` defining its servers alongside its specs, so
   spec+project-layer is self-contained across installations, modulo
   secrets — which stay in the user/env layer (the twelve-factor split:
   definitions travel with code, credentials stay local).

### Pillar 3 — No hidden state: the transcript is a file the caller owns

Intent note, recorded 2026-07-07: per-cwd auto-continuity was designed for
a loop that no longer exists — interleaving `nb "..."` calls with other CLI
commands, the shell acting as the agent loop and the directory as the
conversation. Once nb grew a bash tool, that loop became absurd: the model
runs the CLI commands itself, inside one invocation.

Decision (same date): **durable sessions are dropped entirely** — not
demoted, removed. Nobody uses nb as a chat client; it's a tool. In practice
persistent conversation state has been a liability more often than a
feature: stale context poisoning later runs, accumulated trash context, and
ritual deletion of history files between evals. An earlier draft of this
pillar kept "named sessions" as an explicit resource; even that recreates
the liability one level up — a state dir to clean, a lock to contend.

- **Every invocation is stateless.** No history read or written, no lock
  file, no `.nb_active_kits.json`, no state dir. `HistoryLock` and the
  per-directory state files delete outright. Interactive mode holds its
  conversation in memory for the life of the process, nothing more.
  Parallel runs anywhere just work.
- **Continuity, when wanted, is explicit transcript passing.** The caller
  keeps the transcript and hands it back:
  `nb --output jsonl "..." > t.jsonl`, then `nb --seed t.jsonl "..."`.
  The caller owns the state, so the caller decides when it's stale — the
  failure mode that motivated ritual session deletion structurally can't
  occur. Interactive mode gets `/save <file>` for the same move. If a real
  cross-invocation need ever reappears, named sessions can return as sugar
  over seed files; nothing else in this plan depends on them.
- **`Seed:` — fabricated or recorded prior rounds as first-class input.**
  A spec field (or `--seed <file>`) loading a transcript as the
  conversation's premise: user turns, assistant turns, tool calls and
  their results — "you called this tool and got this back; now what?" The
  file format is the **same typed JSONL schema `--output jsonl` emits**
  (Pillar 4): the output contract doubles as the input contract, so
  record → edit → replay is the native workflow, and one invocation's
  output becomes the next one's seed. It complements fake tools: fake
  tools script responses to calls the model makes *next*; a seed scripts
  the rounds that supposedly *already happened*. The primary use case is
  lying about **MCP/external tool responses**; for filesystem state, use
  real files in a scratch dir instead of fabricated file-tool rounds.
  Seeds get the same validation standard as specs — tool_call/tool_result
  pairing checked at load, errors specific enough to fix from the message
  alone.
- **Seeds do not relax the read-before-edit guard.** A seeded `read_file`
  round does not populate `FileReadTracker`: the guard exists to catch
  divergence between the model's belief and the disk, which is exactly
  the state a fabricated read creates. The agent re-reads before editing —
  realistic behavior, not a crack in the illusion (and consistent with
  "use real files for filesystem state").
- **Mid-turn seeds are deferred.** A transcript ending on an unanswered
  tool call ("the result is arriving now") would be the strongest form of
  the trick, but it requires the loader to resume inside the tool-dispatch
  loop rather than prepend messages. v1 requires seeds to end on a
  completed round; revisit after the more pressing tool-shape problems are
  solved.
- **One transcript format.** Seed input and jsonl output share the public
  schema; the internal Type-discriminated history serialization retires
  with the files that used it. (Known limitation carried over: images
  don't round-trip.)

### Pillar 4 — The I/O contract: machine-first output, deterministic input

This pillar adopts `plans/headless-machine-output.md` wholesale (porcelain
first, JSONL second, output-sink seam, verbatim fences) and extends it with
the contract pieces that plan didn't cover:

- **stdout/stderr discipline.** In porcelain/jsonl modes: stdout carries the
  answer (or the event stream); *all* chrome — banners, trust preamble, kit
  messages, MCP connect progress, tool noise in porcelain mode — goes to
  stderr. `--dump-tools` already models this.
- **Exit-code contract.** Proposed:
  `0` success (final answer produced) · `1` startup/config error ·
  `2` provider/model error mid-turn · `3` turn aborted (MaxToolCalls,
  tool-error limit, doom-loop) · `4` approval required but policy denied and
  the task couldn't complete · `5` history-lock conflict (transitional —
  the lock itself is deleted in Phase 3). The aborts that
  today inject a canned message and exit 0 (`ConversationManager.cs:303`)
  become visible to `$?`.
- **Non-interactive approval is a policy decision, never a prompt.** When
  stdin isn't a TTY (or output mode ≠ interactive), every approval point
  consults the run spec's approval policy and returns allow/deny
  deterministically. A deny is reported to the model as a structured tool
  error (it can route around it) and counted; it never blocks the process.
  This also finally closes the open TODO: approval patterns extend to MCP
  tools and native file tools, not just bash.
- **Result telemetry.** The final JSONL event (and a porcelain trailer line
  on stderr) reports `{turns, tool_calls, input_tokens, output_tokens,
  duration_ms, exit_reason}` from `UsageDetails`. Weaver currently cannot
  compare cost across harness legs at all.
- **The JSONL schema is symmetric.** The same typed event schema is the
  seed-input format (Pillar 3) — what nb can emit, nb can be told already
  happened. The exact record shapes, the round-trip contract, and the
  precise limits of that symmetry (it's output-superset / seed-subset, not
  identity) are designed in `plans/transcript-schema.md` — a keystone the
  umbrella plan depends on but does not itself specify.
- **Fail fast, never wizard, never hang.** Already the stated design in
  `plans/onboarding-and-kit-ux.md` (no first-run wizard into a pipe); this
  plan makes it a rule for every interactive affordance: skill-load
  confirmations, provider pickers, OAuth browser flows all degrade to a
  structured error in non-interactive modes.

### Pillar 5 — One contract, three surfaces: the library facade

Added 2026-07-07, after `Process.Start`-from-C# was tried and pronounced a
dumpster fire (it is: quoting, stream deadlocks, async ceremony, stringly
results, temp-file lifecycles). .NET utility code — including the
downstream projects nb is explicitly a proving ground for — consumes nb
**as a library**:

```csharp
var spec = new RunSpec {
    Extends = "headless",
    Tools = new() { McpServers = ["weather"] },
    Seed = Transcript.Load("fake-history.jsonl")
};
var run = await Nb.RunAsync(spec, "do the task");
// run.Events (typed transcript), run.Answer, run.Usage, run.ExitReason
```

The rule that keeps this from reopening the stable-library objection:
**the facade is isomorphic to the published contract — nothing more.**
`RunSpec` is the spec schema as a type; `run.Events` is the JSONL schema
as records; `ExitReason` is the exit-code table as an enum; streaming is
`IAsyncEnumerable<TranscriptEvent>` — still the event schema. No engine
types cross the boundary, ever: no `ConversationManager`, no `IChatClient`
leakage, no mutable conversation objects. One contract, three surfaces —
process/CLI, in-process library, and (eventually) the REPL as a skin over
the same runner. Adding a spec field grows all three together.

Consequences:

- **The CLI becomes a thin shell over the facade** — `Program.cs` builds a
  `RunSpec` from flags/files/env and calls the same `Nb.RunAsync`. This
  gives the Phase 1–3 seam-insertion work a destination, and it does
  **not** require splitting the monolith: the facade wraps
  `ConversationManager` whole; the internal mess stays internal.
- **The spec file is one serialization of the spec, not its identity.**
  C# callers build the object (no JSON touches disk); bash callers write
  files because files are bash's objects; models write files because
  documents are what models author well. The earlier "per-run JSON feels
  like CLI args in a file" jank was the missing-transport symptom — a C#
  program forced to serialize an object so a process could immediately
  deserialize it.
- **Packaging** follows the `nb.Providers.Abstractions` precedent: a
  NuGet (`nb.Core` or similar), which also forces the provider-loading
  path (currently executable-relative `providers/` + ALC) to work for
  library hosts — config objects in, no install-dir assumptions. That
  work rides with Phase 2's config layering.

### Prerequisite, not pillar: a real sandbox

`bugs/shell-tool-no-filesystem-sandbox.md` stays its own work item, but this
plan takes a position: **an OS sandbox is a prerequisite for promoting
unattended `--trust` use**, because automation widens the blast radius of
both known holes. The spec schema reserves `Approval.Sandbox:
"none" | "bwrap"` so a spec can declare that its bash tool spawns under
`bwrap` (cwd + tmp writable, repo-external paths read-masked) on platforms
that have it. Weaver's external jail then becomes nb's internal default for
headless specs — one less thing every consumer rebuilds.

## Worked example: an eval harness

A concrete pressure test (2026-07-07) — fake prior rounds, a grader, one
MCP tool exposed approval-free, no bash/file tools, output captured and
reformatted into the grader prompt. Everything lands in specs + seeds +
composition; **zero hooks** — hooks are only for nb calling *out* mid-run,
and nothing here is that.

```jsonc
// agent.json
{
  "Extends": "headless",
  "Tools": { "Native": [], "McpServers": ["weather"] },
  "Approval": { "McpTools": ["weather/*"] },
  "Seed": "fake-history.jsonl",
  "Output": "jsonl"
}
// grader.json: { "Extends": "headless", "Tools": {}, "Prompt": { "Base": "grader.md" } }
```

```bash
nb --spec agent.json "do the task" > run.jsonl
answer=$(jq -r 'select(.type=="assistant_text").text' run.jsonl)
calls=$(jq -c 'select(.type=="tool_call")' run.jsonl)
printf 'ANSWER:\n%s\n\nTOOL CALLS:\n%s\n' "$answer" "$calls" \
  | nb --spec grader.json
```

Two clarifications this example forced:

- **The harness language is the caller's choice.** Bash composes nb as a
  process (above); a C# harness consumes nb as a library (Pillar 5) —
  `RunSpec` object in, typed `run.Events` out, no serialization, no
  subprocess ceremony. Both are the same contract on different
  transports. (`Process.Start` from C# was tried and rejected — see
  Pillar 5.)
- **Approval schema requirement:** the `Approval` block needs per-server /
  per-tool MCP granularity (`"McpTools": ["weather/*"]`), not just a
  global default — this example is the acceptance case for that field.

## Design lineage (PLT notes)

"A REPL that can also run programs" is a solved problem — several times,
convergently. No single language is stealable whole (nb's "expressions" are
prompts, not a term language), but the invariants transfer:

- **One semantics, many arrival modes** (sh, python, perl, ruby): REPL vs
  file vs `-c` vs piped stdin differ in *affordances* (prompt, history,
  echo), never in *meaning*. Specs complete nb's missing "file" leg.
- **The rc-file rule** (bash): interactive conveniences live in a config
  layer non-interactive invocations never load — why `ssh host cmd` is
  predictable. The `chat` built-in spec is nb's `.bashrc`; headless runs
  must never load it implicitly.
- **Formatting at the boundary, structure inside** (PowerShell): the
  pipeline carries typed objects; rendering happens only at the terminal
  edge. Independent validation of Pillar 4's sink/jsonl design.
- **Source beats image** (Smalltalk images, Jupyter hidden kernel state):
  durable session state rots and demands ritual deletion; file-based,
  re-runnable artifacts win. Validates Pillar 3's statelessness.
- **The transcript is the artifact** (Coq/Isabelle proof scripts): work
  interactively, save the dialogue, re-run it in batch. Record → edit →
  replay is the proof-script loop; mid-turn seeding is Proof General's
  prefix replay, so the deferred feature has a known implementation shape.
  `expect` is the same genre: fake tools script the future, seeds script
  the past.
- **Don't embed a diminished host language** (make recipes, Dockerfile
  `RUN`): control flow belongs to the calling shell. `Task` + `{arg}`
  placeholders is the boundary; specs never grow conditionals or loops.
- **In-distribution is a design criterion** (added 2026-07-07). Coding
  models are a primary author of specs and pipelines, and what models
  fumble is rarely syntax — it's zero-corpus API surface. So: borrow
  surfaces that have a corpus (JSON-under-schema, shell conventions,
  type-tagged JSONL), and never invent composition syntax. A bespoke
  "stack nb commands in one file" format would be the worst quadrant:
  out-of-distribution *and* a workflow runtime to maintain. Embedding a
  real language (Lua/JS) is a trap for the same reason — familiar syntax
  around zero-corpus nb bindings pays the complexity without buying the
  familiarity. Bash-around-nb is the one composition layer where nb's
  weirdness is invisible, because nb appears there as just another CLI
  command — the pattern with the largest corpus of all. Spend the
  weirdness budget on unavoidable semantics (field meanings, event
  types); spend zero on syntax.

The distilled principle: **batch semantics is the core calculus; the REPL
is sugar over it.** Today's nb is the inversion, and this plan is the
reversal. Operational test: every interactive affordance must be definable
as a spec field or transcript event, so a session can always be exported as
the program that reproduces it. The library facade (Pillar 5) is the same
principle's third face: CLI, REPL, and library are three skins over one
runner, and the contract (spec in, events out) is identical on all three.

Two concrete steals adopted from this survey:

1. **Shebang-executable specs.** The spec parser tolerates a leading `#!`
   line, so `#!/usr/bin/env -S nb --spec` makes a `Task`-bearing spec file
   directly executable: `./nightly-triage.nb`.
2. **`/save` exports the session as artifacts** — not just the transcript
   (seed) but also the *effective run spec* reproducing the session's
   current setup (provider, kits, tool surface). Fiddle interactively,
   export the script: the REPL becomes the spec-authoring tool.

## What this costs the chat/coding-agent identity

Stated honestly, since the prompt for this plan explicitly accepts the trade:

- **Config moves.** One-time migration for existing installs (keys out of
  the bin dir — which is a security fix wearing a breaking-change costume).
- **All durable conversation state goes away.** `.nb_conversation_history.json`,
  the lock file, and `.nb_active_kits.json` stop existing; an interactive
  restart starts fresh. Continuity is explicit — `/save` + `--seed`, or
  capturing the jsonl stream. This is the plan's sharpest break, accepted
  because in practice that state demanded ritual deletion more often than
  it helped.
- **Kits demote** from "the configuration mechanism" to "prompt/tool
  overlays." The `+kit` UX is unchanged, but new configuration energy goes
  to run specs.
- **Interactive polish freezes.** `plans/console-output-design.md`'s
  Spectre-chrome direction (rules, gutters) is deprioritized; the
  interactive renderer stays as-is while the porcelain/jsonl paths are
  built. UglyPrompt/completions work continues only as time permits.
- **The coding-agent niceties** (read-before-edit ceremony, apply_patch
  style wars, `/edit`) are maintained but no longer drive design.

Explicit non-goals: A2A (already deferred in `plans/A2A_Support.md` — being
a good composable CLI *is* the delegation story); a daemon/server mode;
parallelism inside nb (callers parallelize invocations — nb's job is to make
that safe, which Pillar 3 does); **any multi-step/workflow format** —
no `steps:`, no pipelines-in-specs, no scripting *of composition*. The
composition language is the host shell, both on PLT grounds (see Design
lineage) and because bash is in-distribution for the coding models that
will author these pipelines, while any format we invent has a corpus of
zero. (Scripted *extension points* are a different question — see
"Scripted extension points" below — and are allowed precisely where the
alternative is inventing a DSL.)

## Scripted extension points — hooks first, csx only under duress

There are seams where declarative config hits its expressive ceiling and
the realistic alternative is inventing a DSL (the true worst quadrant:
zero corpus + a language to maintain). nb already has one growing in-repo:
the fake-tool macro system (`{{$choice}}`, `{{$counter}}`, `{{$param.x}}`)
is a tiny embedded language accreting features — the make-recipe
anti-pattern in progress. The seams that need a code escape hatch:

- **Approval predicates** — the long tail past allow/deny lists ("writes
  only under src/, deny anything touching .git, this MCP tool only with
  these argument shapes"). Slots into Phase 5.
- **Fake-tool handlers** — computed responses, capping the macro language
  at its current size permanently.
- **Ceremony-free custom tools** — a tool without the
  plugin-DLL/AssemblyLoadContext ritual.

**First instinct was in-process csx via Roslyn** — hosting is trivially
cheap, C# is the host language, compiler errors give models a correction
loop. But the objection that killed it as the default (2026-07-07): the
cost was never execution, it's that nb would suddenly own a **friendly,
documented, stable library contract** — a product it has never had.
(`IChatClientProvider` survived as nb's only public contract precisely by
being one thin interface delegating to Microsoft's types; script globals
face constant "just expose a little more context" pressure, and every
convenience is contract forever.)

**The default mechanism is therefore hooks** — external commands in the
git-hook / Claude-Code-hook shape: nb spawns the command named by the
spec, pipes it a typed event on stdin (the *same* transcript schema
Pillars 3/4 already publish — a `tool_call` event in, a verdict or
`tool_result` event out), reads the reply. What this buys:

- **No second contract.** The extension API *is* the transcript schema
  we're committed to documenting and versioning anyway. One schema, four
  uses: output stream, seed input, session export, hooks.
- **Language-agnostic and in-distribution.** The hook shape has an
  enormous corpus; and anyone who wants C# writes their hook in csx via
  `dotnet-script` — nb never hosts Roslyn.
- **A process boundary, not an address-space guest** — naturally
  sandboxable, crash-isolated.

Cost: a process spawn per invocation — irrelevant for approval decisions;
fake tools are eval-only, so likely irrelevant there too. In-process csx
is retained only as a **measured fallback**: adopt it for a seam only if
hook spawn latency demonstrably hurts there, and then with a
deliberately tiny data-only binding surface (small records + enums, never
engine types).

Explicitly *not* admitted in any variant: scripting as spec format
(JSON-under-schema is more in-distribution and `--resolve`/`--validate`
is a better teaching loop). Composition from .NET code goes through the
Pillar 5 facade (schema-isomorphic types only) — what stays forbidden is
exposing *engine internals* (`ConversationManager`, provider clients,
mutable conversation state) in any scripting or library surface.

**Trust caveat, non-negotiable:** a hook command (or csx file) referenced
by a spec is arbitrary code execution. Human-authored hooks carry the
same trust as the bash script invoking nb; but a model-authored spec
pointing at a model-authored hook is the model escaping the approval
system by writing config. Hook paths are trust-gated like writes — never
auto-approved out of a model-generated spec.

## Phasing

**Status (2026-07-11).** Design ratified: this plan and its two children
(`conversation-program-evaluator.md`, `transcript-schema.md`) are now
`state: active`, and the transcript-schema record-shaping decisions are locked
(verbose event names, JSON-typed arguments, full-output-by-default, two-file
`/save`, sibling `mcp`/`tools` config verbs). **Landed early, out of phase
order:** Phase 3's *deletion half* — `HistoryLock`, `.nb_conversation_history.json`
save/load, and `.nb_active_kits.json` persistence are removed; every invocation
is already stateless. Next up is the schema keystone (transcript-schema S1:
types + serializer + golden round-trip tests), which both Phase 3 (seeds) and
Phase 4 (jsonl) build on.

**Status (2026-07-14).** Phases 0–3 are landed, plus Phase 4's core (jsonl +
telemetry arrived early via the S2 transcript seam and the exit-code contract).
The "specs, not switches" thesis runs as code: layered config, `--program`/
`--spec` (incl. computed `chat`/text `headless`), `--validate`/`--resolve`,
mid-stream provider/model swap. **The Phase 3 tail is cleared:** the default
preset is a first-class directive list routed through the evaluator (the
prompt-floor invariant, now `rules/preset-floor.md`); `--seed` keeps a
transcript's own `system` messages; the `mcp`/`tools` directives reshape the tool
surface (`plans/tool-surface-directives.md`, strict-empty MCP for programs);
`tool_call`/`tool_result` turns evaluate as fabricated premise; and the result
trailer sums token usage across all runs. The one deferred item, the `approval`
directive, is deliberately **Phase 5** work (it ships with the approval-policy
object + sandbox).

**Status (2026-07-15).** Phase 5 is done (declarative approval policy + the bwrap
bash sandbox) and the Phase 2 remainder landed: friendly env aliases
(`NB_PROVIDER`/`NB_MODEL`/`NB_OUTPUT`/`NB_SPEC`) and layered `mcp.json`
(install → user → project, merged by server name). **Remaining: only Phase 6**
(the library facade as a package).

Ordered so each phase delivers standalone value and the weaver harness can
shed a hack at every step.

- **Phase 0 — honesty patches (small, ship independently).**
  Color/ANSI gate on `Console.IsOutputRedirected` + `NO_COLOR`; route
  existing chrome to stderr; exit-code contract; non-TTY approval points
  fail deterministically instead of hanging; lock conflict becomes an error.
  *Weaver sheds: exit-code blindness, half the ANSI stripping.*
- **Phase 1 — porcelain output.** Per `headless-machine-output.md` phase 1:
  `--output porcelain`, verbatim fences, `TOOL`/`RESULT` lines, output-sink
  seam in `ConversationManager`. *Weaver sheds: brace-scanning entirely.*
- **Phase 2 — config layering + env vars + `--config`.** User/project/env
  layers; keys out of the install dir; provider/model finally selectable
  per invocation (via env/layers — still no flag soup).
  *Weaver sheds: Debug-bin-dir coupling, keys-in-jail hazard; model choice
  no longer requires editing global config (the SSH model-swap remains,
  since that's about what's loaded on the GPU box, not about nb).*
- **Phase 3 — run specs + statelessness + seeds.** The spec schema,
  `--spec` (path or name), `NB_SPEC`, `Extends`, built-in `headless`/`chat`
  specs, `--validate`/`--resolve`; deletion of the history/lock/kit-state
  files and `HistoryLock`; `Seed`/`--seed` transcript input (complete
  rounds only); shebang-tolerant spec parsing; interactive `/save`
  (transcript + effective spec). The transcript schema is defined
  once, shared between Phase 3 (seeds) and Phase 4 (output stream) —
  whichever lands first brings the schema with it (specified in
  `plans/transcript-schema.md`, which should be ratified before this phase
  starts code). This is the big one and lands the "specs, not switches"
  thesis.
  **Load-bearing invariant for this phase:** the *prompt floor* is the
  **default preset** carrying the prompt layers as explicit `system`
  directives, loaded only on the human/`-p`/bare path (the rc-file rule).
  `system` is a plain message, not a singular owned prompt (see
  `transcript-schema.md`'s "The system message and the prompt floor"): nothing
  is dropped or injected, so "missing prompt" is impossible on the floored path
  and, on an explicit program, is a deliberate choice (a preset-less program
  gets no system message — correct for harnesses). The invariant to hold is
  that the default preset always resolves to the baseline layers; worth a
  `rules/` entry when the preset resolver is written.
- **Phase 4 — JSONL + telemetry.** `--output jsonl` typed event stream with
  `assistant_json` and the result-telemetry event.
- **Phase 5 — approval policy + sandbox.** Declarative per-profile approval
  (unifying `--approve`, `alwaysAllow`, trust, and MCP/native coverage);
  `bwrap` integration at the bash spawn point.
  *Weaver sheds: the external jail.*
- **Phase 6 — the library facade as a package.** The facade *types*
  (`RunSpec`, `TranscriptEvent`, `ExitReason`) are born in Phase 3 — they
  are the spec/transcript schemas' implementation, and the CLI starts
  consuming them internally then. Phase 6 is the packaging step:
  `Nb.RunAsync` public, NuGet published, provider loading working for
  library hosts (with Phase 2's config objects). Shipping it last means
  the contract has survived four phases of internal use before anyone
  outside can take a dependency on it.

Engineering note for phases 1–5: the imp learning warns that
`ConversationManager.cs` (2126 lines) resists splitting — streaming, tool
merging, and history share mutable state. The strategy here is deliberately
**seam insertion, not decomposition**: an output sink and an approval-policy
object get injected at the ~40 existing call sites; the class isn't split
until the streaming extraction settles.

## Acceptance test

The reorientation is done when the weaver nb leg reduces to:

```bash
nb --spec ./eval-runner.nb.json "<prompt>" > events.jsonl
```

where the spec is a file the harness generates itself — with no bwrap
wrapper, no ANSI stripping, no brace-scanner, no `HOME`
override, no bin-dir mounts, a meaningful `$?`, and a token count in the
final event — i.e., when nb's leg of the harness is as boring as claude's.

## Related documents

- `plans/conversation-program-evaluator.md` — reframes Pillar 1 (a spec is a
  reusable directive-prefix, not a separate artifact) and Pillar 5 (the
  `Nb.Program()` builder); recasts nb as an evaluator whose bytecode is the
  transcript schema. Read alongside Pillar 1.
- `plans/transcript-schema.md` — the one wire format underpinning Pillars
  3/4/5 and hooks; specifies what this plan only names.
- `plans/headless-machine-output.md` — adopted as Pillar 4 phases 1/4.
- `bugs/shell-tool-no-filesystem-sandbox.md` — prerequisite for Phase 5.
- `plans/onboarding-and-kit-ux.md` — its non-interactive rules generalize
  here; its kit items 2/4/5 should be re-read against run specs before build.
- `plans/Approval_Enhancements.md` — risk-scoring would slot into the
  Phase 5 policy object if ever built.
- `plans/A2A_Support.md` — records why composable-CLI is the delegation
  strategy.
- `rules/model-policy-in-prompt-layers.md` — constraint honored by Pillar 1.
