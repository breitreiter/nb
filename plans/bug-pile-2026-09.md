---
kind: plan
title: Working order for the open bug pile (14 reports, 2026-09-19)
created: 2026-09-19
updated: 2026-09-19
status: accepted
state: batch 0 done; batch 1 done (5 providers, not 2); batch 2 remaining
touches:
  files:
    - nb.Core/Transcript/TranscriptEvent.cs
    - nb.Core/Transcript/TranscriptSerializer.cs
    - nb.Core/Transcript/TranscriptMapper.cs
    - nb.Core/Facade/Nb.cs
    - nb.Core/RetryingChatClient.cs
    - Providers/OpenAI/OpenAIProvider.cs
    - Program.cs
    - nb.csproj
---

# Working order for the open bug pile

42 reports in `bugs/`; 25 fixed, 3 wontfix, **14 open**. This plan is the order to
work them and, more importantly, *which ones must not be worked one at a time*.

| sev | cluster | report |
|---|---|---|
| high | provider-truthfulness | `Sdk_Retry_Policy_Multiplies_Every_Model_Call` |
| medium | provider-truthfulness | `Rate_Limit_Exhaustion_Hides_Its_Own_Cause` |
| medium | provider-truthfulness | `Effective_Model_Is_Not_On_The_Trailer` |
| medium | schema-vs-dispatch | `Trailer_Never_Carries_Duration` |
| medium | transcript-replay | `Seed_And_Program_Turn_Numbers_Collide` |
| medium | transcript-replay | `Feature_Resume_A_Run_From_Its_Log` |
| low | schema-vs-dispatch | `Assistant_Json_Event_Is_Never_Emitted` |
| low | feature-gap | `Feature_Injected_Reminders_Carry_A_Source_Tag` |
| low | provider-truthfulness | `Feature_Trailer_Carries_Program_Hash_And_Nb_Version` |
| low | provider-truthfulness | `Feature_Trailer_Carries_Cost_When_The_Entry_Declares_A_Price` |
| low | feature-gap | `Feature_Sample_Seed_Directive_For_Providers_That_Accept_One` |
| low | cli-surface | `Feature_Version_Flag` |
| low | distribution | `Feature_PackAsTool_Carries_Providers_Into_The_Tool_Package` |
| low | distribution | `Feature_Release_Workflow_Publishes_To_Nuget` |

## The two facts that set the order

**Five reports edit the same forty lines.** `Effective_Model`, `Duration`,
`Program_Hash_And_Nb_Version`, `Cost` and the trailer half of `Sample_Seed` all widen
the identical chain: `ResultEvent` → `TranscriptMapper.ResultTrailer`'s parameter list
→ `WriteResultBody` → the reader → `RunResult` → the trailer table in
`docs/conversation-program-cli.md` → an `evals/run.sh` jq assertion. Worked serially
that is five passes over one parameter list and five docs-table edits. Worked as one
batch it is one widening, one table, one eval block. **Batch them.**

**Nine of the fourteen are proctor's.** Eight filed 2026-09-17 from proctor plus
`Effective_Model`, which proctor also needs. The trailer batch and the version spine
are almost entirely "make a transcript self-describing so a sidecar experiment manager
doesn't keep a drifting side-table" — which is the same argument that put `provider` on
the trailer. If proctor is the near-term consumer, batches 0–2 are the whole
deliverable and everything after is optional.

## Batch 0 — the version spine (prerequisite, closes nothing) — DONE 2026-09-19

`Directory.Build.props` at the root with `<Version>`, and let the SDK emit
`AssemblyInformationalVersion` despite `GenerateAssemblyInfo=false` in both csprojs
(hand-write `nb.Core/AssemblyInfo.cs` if that interaction is awkward).

Four reports independently ask for this and each proposes its own version source —
`Version_Flag`, `Program_Hash_And_Nb_Version`, `PackAsTool`, `Release_Workflow`. Pick
once, in props, so the flag, the trailer field and the package id cannot disagree.
Half an hour, unblocks batches 1 and 4.

## Batch 1 — the rate-limit pair (start here; the only `high`) — DONE 2026-09-19

Do `Sdk_Retry_Policy` **first and alone**: `options.RetryPolicy = new
ClientRetryPolicy(maxRetries: 0)` in `OpenAIProvider.OpenAIOptions()` and the same in
`AzureOpenAIProvider`. The regression is a fake transport counting requests — a call
that always 429s should hit it `MaxRetries + 1` times, red at `4x` before the fix.
This is the repo's write-the-failing-test-first case exactly: a counted observation,
not a guess about an interface.

Then audit every other plugin that builds an SDK client for an inherited retry
pipeline. The defect is "we took the SDK's defaults", not anything OpenAI-specific.

`Rate_Limit_Exhaustion` then splits three ways, and the reframe below (2026-09-19)
changes what survives of it:

1. **Print the text `RateLimitClassifier` already classified on**, not `ex.Message`.
   Kept: this is a human reading stderr on a dead run, not a consumer parsing time,
   and today it renders `Service request failed` while the body that decides the whole
   diagnosis is in hand and discarded. ~10 lines, fully separable.
2. **Read `Retry-After` / `X-RateLimit-Reset` off the raw response** before falling
   back to prose, and abandon when the hint exceeds the remaining budget. Kept, and
   **promoted to a hard prerequisite** of item 3 — see the sequencing note.
3. **Stop charging `PaceAsync` to the retry budget.** Kept; the reporting half of it
   (`retrying in 2.1s (pacing 16s)`, naming the binding limit) is **dropped** per the
   reframe. See below.

### Reframe (2026-09-19): one number, not a breakdown

The owner's framing, which supersedes the accounting debate in the report: what nb
owes a consumer is *correct time spent waiting on a provider, for whatever reason,
inclusive of waits and retries* — as a single bucket, not a decomposition. Consumers
do not care **why** a provider was slow. A slow, expensive db query inside a tool call
is actionable; Anthropic having a bad afternoon is noise. The only thing worth being
able to see is the top line: "this run took 40 minutes and 35 of them were the
provider", so the number can be discounted rather than investigated.

That kills the detailed reporting and **merges this item with
`Trailer_Never_Carries_Duration`** — they want the same instrumentation.

Two raw fields on the trailer, not a breakdown:

- `duration_ms` — total wall time.
- `provider_ms` — all time blocked on a provider: inference, pacing, backoff, retries,
  and the oracle's side call.

The consumer subtracts. Emit both raw rather than the difference — a measurement is
more honest than a derivation — but the difference is what anyone reads:
`provider_ms` close to `duration_ms` means there is nothing to learn; a wide gap means
the agent's own work is where the time went.

**Inference counts, not just overhead.** Separating "slow because overloaded" from
"slow because it is a large reasoning model" is exactly the breakdown that has no
value, and both are equally unactionable.

### Why this is cheaper than it sounds

The measurement point already exists and is the class the budget fix modifies anyway.
`RetryingChatClient` is a `DelegatingChatClient` installed at exactly one site
(`ProviderManager.cs:141`), wrapping every client nb builds. `PaceAsync`, the inner
call, the backoff `Task.Delay` and the loop around all three are inside its
`GetResponseAsync`. A stopwatch around that loop body **is** the measurement. It also
catches the oracle for free, since `SideCallAsync` takes a client from the same path
(`ProgramEvaluator.cs:196`) — correct, because the judge is wall-clock the consumer
paid for without asking for it.

Un-charging pace from the retry budget then comes nearly free: the paced total is a
number the instrumentation is already accumulating.

### Hazards

- **`Wrap` does not always wrap.** `RateLimitRetry.cs:216` returns the client
  untouched when retry and pacing are both off, which would leave `provider_ms`
  silently reading zero — the field-wrong-about-its-own-label failure this repo keeps
  filing. It has to always wrap.
- **Streaming over-counts.** `GetStreamingResponseAsync` yields as updates arrive and
  `ConversationManager` works between yields, so a stopwatch around the enumerator
  charges the caller's processing to the provider; only time inside `MoveNextAsync`
  counts. **Superseded by the decision to remove streaming entirely** (TODO.md,
  "Chat-era surface cull", 2026-09-19). If streaming goes first this hazard never
  needs solving; if it has not gone yet, measure inside `MoveNextAsync` and delete
  that care along with the streaming path.
- **Sequencing, not optional.** Charging pace to the budget was accidentally acting as
  a brake. Remove it without item 2 in place and the pathological case gets *longer*
  before it gets better — in the report's own repro an uncharged budget would have kept
  retrying a daily quota with hours left on it, past 300s toward the attempt cap. Land
  items 2 and 3 as one change.

### Design decision

Where the counter lives. Provider switches mid-program mean several clients over one
run, so per-client state read at the end does not compose. Pass a run-scoped
accumulator into `Wrap` and have each client `Interlocked.Add` into it — handles
switches and concurrent library hosts at the cost of one parameter. The alternative,
a separate `MeasuringChatClient` layered outside the retry client, separates the
concerns better but adds a delegating layer for one number, which is the scaffolding
CLAUDE.md says not to build.

### Test note

`RetriesStopAtTheWallClockBudget_NotTheAttemptCap` (`RetryingChatClientTests.cs:70`)
sets no `MinRequestIntervalMs`, but `RaisePace()` starts pacing at 1s after the first
throttle regardless — so pace is being charged there today. Uncharged, it runs more
retries and lands near ~7s against its `elapsed < 10s` assert. It will pass with less
headroom than CI wants; pin its pace explicitly rather than let it drift toward flaky.
The new case fits the suite's existing wall-clock style (1s cap, 2s budget per the
file header): `MinRequestIntervalMs: 500`, `RetryBudgetSeconds: 2`, `MaxRetries: 3` —
charged, pacing eats the budget and you get ~2 calls; uncharged, you reach the attempt
cap at 4. About 3 seconds, red before the fix.

## Batch 2 — one pass over the transcript layer

All of these touch `TranscriptMapper.FromHistory` / `ResultTrailer` / `WriteResultBody`.
One branch, one docs-table edit, one eval block. Order within:

1. **`Effective_Model`** — `ConversationManager.GetCurrentModel()` reading
   `client.GetService<ChatClientMetadata>()?.DefaultModelId`, *not* the program's
   requested model. Carries a prerequisite: `MockProvider.GetService` returns null
   unconditionally while the class already exposes `ChatClientMetadata` — fix Mock to be
   honest, or the field is absent from every test and eval. The assertion that matters
   is the program with no `model` directive and an entry with no `Model` field, proving
   the trailer reports the plugin's hard-coded default.
2. **`Duration`** — **moved to batch 1** (2026-09-19). `DurationMs` is declared,
   written and read, and nothing sets it; it lands with `provider_ms` because they are
   one instrumentation pass. The report's suggested `tool_ms` split is **dropped** —
   `duration_ms - provider_ms` already answers the question a split was for, without
   nb having to decompose anything.
3. **`Program_Hash_And_Nb_Version`** — needs batch 0. Hash the *resolved* event list
   (`@file` expanded, `--seed` spliced), not the source text.
4. **`Cost`** — two optional priced fields on the provider entry, accumulated per
   round-trip at the price live at that moment rather than by multiplying the summed
   usage by the last entry's price. Inherits `usage.estimated`; no second flag.
5. **`Assistant_Json`** and **`Injected_Reminders_Source_Tag`** — both land in
   `FromHistory`, both are enrichment, both are ignored on seed-load. Take them here
   because the signature is already open. For reminders, generalise the reference-keyed
   `_oracleAnswers` map to an injected-source map; wire value is `"todo"` singular even
   though the turn-dump label says `"todos"`.

`evals/run.sh --skip-llm` is not optional for this batch — every item changes a string
a consumer reads, which is exactly the class `dotnet test` passes and CI fails.

## Batch 3 — the seed/body turn collision

`Seed_And_Program_Turn_Numbers_Collide` stands alone and is worth fixing on its own:
today a `--seed` program cannot open with a `system` directive, cannot fabricate an
assistant turn after the premise, and cannot build a multi-message turn — only the one
inline `run <text>` shape the docs happen to demonstrate. Renumber the body's turns to
continue from the seed's highest at the point the two are concatenated. That also
closes the quieter case the report flags, where equal turn numbers pass validation and
silently reorder the body's message ahead of the seed's.

Red-first: the failing assertion is an error string a human reads, and the current one
blames the seed file, which is the file with nothing wrong with it.

## Batch 4 — distribution chain

Strictly sequential, all after batch 0: `Version_Flag` → `PackAsTool` → `Release_Workflow`.

`PackAsTool`'s real work is relaxing the `'$(RuntimeIdentifier)' != ''` condition on
`BuildProvidersForPublish` / `CopyProvidersAfterPublish` so a RID-less pack publish
actually carries `providers/`; providers already build without a RID. Do **not** add
`PublishTrimmed` or `PublishSingleFile` — `AssemblyLoadContext` plugin loading is a
documented trimming incompatibility and `McpManager.cs:349` still resolves via
`Assembly.GetExecutingAssembly().Location`, which is empty under single-file. Check
whether the bare `nb` package id is free before choosing it.

Do this batch when nb should be installable by someone who is not us. Not before.

## Batch 5 — `sample seed`

Parser case beside `budget`, `SampleEvent`, a `SetSeed` setter, `ChatOptions.Seed`, a
trailer field, and an ignore-warning in the harness-costume style when the provider
can't honour it. Deliberately after batch 2 so the trailer plumbing is already open.
Open decision the report names: a capability flag on `IChatClientProvider` versus a
fixed list in nb.Core. The flag is honest, the list ships first — and
`IChatClientProvider` is a versioned public contract with an out-of-tree implementer,
so a flag has to be defaulted.

## Batch 6 — resume, and only the half that is safe

`Feature_Resume_A_Run_From_Its_Log` carries its own gate and it should be honoured:
**decide which class of loss is actually costing runs before building.** The feature
covers losses that reach a trailer (`provider_error`, `rate_limited`, budget aborts).
It reaches none of the losses that emit no byte at all — Ctrl-C, a dead MCP server,
`NbStartupException`, a crash. If the observed losses are mostly the second class, the
feature that stops them is incremental emission (`--log <file>` written as events
happen), which is a different and larger change.

Split it as the report proposes:

- **`nb --repair <log>`** — pure transform: drop the trailer, resolve the dangling tool
  call, renumber turns. No provider, no tokens, fully coverable by
  `evals/run.sh --skip-llm`. Buildable now, once batch 3 lands. The one design call:
  delete the unanswered call, or synthesize `[run interrupted; result unrecorded]`.
  Synthesize — a resumed agent told its `bash` call went unrecorded can re-check; one
  whose call vanished cannot know to.
- **`nb --resume <log>`** — blocked on gap 2, which is its own item: the log records no
  envelope, so a naive resume runs recovered history under whatever config default is
  live and produces a mis-attributed transcript that nothing flags. That is worse than
  losing the run. Gap 2 means emitting config directives into the output stream as the
  evaluator applies them, and it has a real snag — replaying a `harness` directive
  re-inserts the costume preamble that is already in the log.

**Do not ship `--resume` before gap 2.** It is the exact failure mode
`Failed_Provider_Directive_Silently_Substitutes` was filed about.

## Recommended sequence

0 (version spine) → 1 (rate-limit pair) → 2 (transcript pass) → 3 (turn collision)
→ 4 (distribution) → 5 (sample seed) → 6a (`--repair`) → gap 2 → 6b (`--resume`).

Batches 0–2 close nine of the fourteen and are the proctor deliverable. If the pile
needs to stop somewhere, it stops after 2.


---

## Progress log

### 2026-09-19 — batches 0 and 1 landed

**Batch 0.** `Directory.Build.props` sets `<Version>0.9.0</Version>` for the whole repo.
Two gotchas worth recording: the per-project `GenerateAssemblyInfo=false` in `nb.csproj`
and `nb.Core.csproj` *overrides* props (props imports first), so those lines had to go;
and the SDK appends the commit sha to the informational version by itself, so the
`git rev-parse` target the plan budgeted for was unnecessary. `nb --version` prints
`0.9.0+<sha>` via `NbVersion.Current` (nb.Core, so the flag and the trailer field cannot
disagree). Three evals, including the stdin case — nb infers a program from piped stdin,
so a `--version` checked after that inference would consume the pipe.

**Batch 1.** `Sdk_Retry_Policy` fixed across **five** providers, not the two the report
named: OpenAI, AzureOpenAI, AzureFoundry, LocalLlm (System.ClientModel, `maxRetries: 3`)
and Anthropic (Stainless, `DefaultMaxRetries = 2`, verified by reflection). Gemini has no
options surface, so it is unaudited rather than clean. A second bypass the report missed:
`OpenAIProvider` took an options-less constructor when no endpoint and no headers were
configured, which would have kept the default policy on the plain api.openai.com path.
Regression observed red at **exactly 4** requests per attempt, independently reproducing
the proxy journal's 36/9 ratio.

`Rate_Limit_Exhaustion` fixed, with the reporting half dropped per the reframe. The
finding worth carrying forward: **pacing does not generally eat the retry budget** —
`PaceAsync` measures from the last request and a backoff has already elapsed since then,
so it only bites once the pace outgrows the backoff. A first regression test with the
floor at or below the backoff cap passed against the unfixed code.

`Trailer_Never_Carries_Duration` closed here rather than in batch 2, since `duration_ms`
and `provider_ms` are one instrumentation pass. `tool_ms` dropped: the subtraction
answers it.

696 unit tests, 110 evals, all green.

### Still open in batch 2

`Effective_Model` (plus the `MockProvider.GetService` returns-null prerequisite),
`Program_Hash_And_Nb_Version` (batch 0 unblocked it), `Cost`, `Assistant_Json`,
`Injected_Reminders_Source_Tag`. All five still land in the same
`FromHistory`/`ResultTrailer` surface, which is now already widened by two parameters.
