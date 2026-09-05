---
kind: plan
title: Bug queue triage — clustering 26 reports into six decisions
created: 2026-09-04
updated: 2026-09-04
status: current
state: active
touches:
  - bugs/
---

# Bug queue triage

26 files in `bugs/`. **14 open, 12 already resolved** (10 Fixed, 2 Closed/will-not-fix)
sitting in the same flat directory, so every triage pass re-reads the closed ones.
That is the first thing to fix, and it is mechanical.

The open 14 are not 14 problems. They are **six clusters**, and three of the clusters
collapse to one fix each.

## Step 0 — hygiene (mechanical, do first) — **DONE 2026-09-04**

Originally proposed moving the 12 resolved reports to `bugs/closed/`. **Rejected**, for
two reasons found while doing it:

- `imp/_meta/conventions.md` says concluded entries stay in a flat folder and use
  frontmatter, *not* a directory split. Bugs are the same human-owned substrate shape as
  plans, so the same rule applies.
- ~30 inbound links point into `bugs/` from `plans/`, `CLAUDE.md`, source comments
  (`QwenCodeHarness.cs:11`, `ConsoleBoundCollection.cs:14`) and tests
  (`ApprovalDenialTests.cs:204`, `McpManagerTests.cs:159`). A move rewrites all of them
  and leaves code comments that go stale on the next move.

**What was done instead:** substrate frontmatter on all 26 reports, one field per line
per `conventions.md` so the `imp/_index/` ripgrep filters work. No file moved, no link
touched.

```yaml
kind: bug
title: '...'          # the H1, verbatim
created: YYYY-MM-DD   # filing date from the Status line
updated: YYYY-MM-DD   # latest date appearing in the report
status: current
state: open | fixed | wontfix
severity: low | medium | high
cluster: <one of the six below>
```

`severity` and `cluster` are triage judgements added by this pass, not claims from the
reports — except where a report states its own severity (`Denials_Do_Not_Name_The_Near_Miss`,
`Bash_Buffers_Unbounded_Output`, `Provider_Config_Cannot_Send_Extra_Headers` all say
medium; `shell-tool-no-filesystem-sandbox` is High per `plans/approval-policy-and-sandbox.md:39`).
Those are carried through as written.

The queue is now a grep:

```bash
grep -l '^state: open$' bugs/*.md                      # the 14
grep -l '^state: open$' bugs/*.md | xargs grep -h '^cluster:' | sort | uniq -c
grep -l '^state: open$' bugs/*.md | xargs grep -l '^severity: high$'
```

Counts: **14 open, 10 fixed, 2 wontfix.**

## Step 1 — verify-and-close pass — **DONE 2026-09-04, closed nothing**

Verified against a fresh build at `0cb2567`. **Neither candidate closed, and the premise
for one of them was wrong.** The pass paid for itself anyway: it produced three report
amendments, one retag, and a scope correction that shrinks cluster 3.

### `Tool_Names_Diverge_From_Model_Native_Surface` — does not close

The plan proposed re-measuring under `harness qwen-code`. **That experiment had already
been run, twice, and was negative** — the report's own "Replication attempt, 2026-08-14"
section records pooled 2/6 vs 4/6, Fisher exact p ≈ 0.57, and an earlier section already
retracted the token claim (<4% movement, not the 98% headline). The triage draft missed
both because it read the reports' opening sections and not their tails. **Lesson for the
next pass: these reports are laboratory notebooks with corrections at the bottom. Read to
the end before scheduling one.**

What the goldens *do* confirm, without a model in the loop, is that the mechanism the
report asked for is fully shipped: `edit`/`glob`/`grep_search`/`list_directory`/
`run_shell_command` names, `file_path` spellings, and the *"Prefer `edit` over
`write_file`"* steer at `nb.Core/prompts/harness/qwen-code.md:64`. Both halves of its
"Suggested fix" landed.

**Retagged** `cluster: harness-evaluation`, `severity: low` — it is a measurement
question, not a schema/dispatch one, and `plans/harness-emulation.md` §"What to diff"
already owns it. That drops cluster 3 back to 3 files.

**Actionable residue, separated out:** the one-sentence `system` steer is the only
intervention on this bug ever demonstrated to work (`edit_file` 1 → 10; exit reason
`token_budget` → `ok`), and it ships only inside the costume preamble — a caller on nb's
native surface gets no steer and no documentation of one. That is a docs change, it does
not wait on the fixture, and it is the cheapest real win in the queue.

### `Bash_Advertises_A_Timeout_It_Ignores` — does not close; widened

Two findings, both meaning the obvious one-line fix is insufficient:

1. **The parameter is advertised `required`, not optional.** From
   `nb.Tests/golden/tool-surface.all-native.txt`, `required` is
   `["description","command","timeout_seconds"]`. A model *must* send a value that is
   then discarded. This is where this report and `Optional_Tool_Parameters` meet on one
   parameter: one says it must be sent, the other says it is ignored, and fixing either
   alone leaves the other standing.
2. **Even once wired, the value can only lower the timeout.**
   `Math.Min(requested, _defaultTimeoutSeconds)` (`BashTool.cs:85`) makes the configured
   default a ceiling as well as a fallback, so the report's motivating case — a model
   asking for longer on a slow build — survives the dispatch fix untouched. Whether the
   clamp is deliberate is an open question for the fix, not for triage.

### `Optional_Tool_Parameters_Advertised_As_Required` — scope shrank

Checking `required` across every golden surface shows the defect is **native-surface
only**. Costumes already declare optionality correctly (`qwen-code` 6 of 9 tools,
`claude-code` 7 of 11, `codex` 3 of 4), because they hand-declare through
`SchemaBuilder.Add(..., bool required = false)` — optional unless opted in — while the
native surface reflects lambdas through `AIFunctionFactory`, where required is the
default and no lambda carries a C# default value. Opposite mechanisms, opposite defaults.

This supersedes the report's "it blocks costume fidelity" bullet: costumes match their
targets' schemas today. The bug costs nb's *own* surface fidelity, which is why it stays
`severity: low`.

### Verification state after this pass

`dotnet build` clean, `dotnet test` 576 passed, `./evals/run.sh --skip-llm` 69 passed.
Note that `nb.Core/ConversationManager.cs` carries uncommitted model-visible string edits
belonging to `plans/oracle-resolver.md`; the evals pass with them in place.

## Test policy for this queue

Per `CLAUDE.md` ("Fixing a bug: write the failing test first — when it's worth it"):
write the regression test first **and observe it fail** where the test encodes an
*observation*; skip it where it would encode a *guess*, or where an honest red state is
disproportionate. Each cluster below carries a **Tests:** line saying which it is, so the
decision is made at triage time rather than argued per-fix.

Two failure modes this is guarding against, both visible in the closed reports:

- A fix that lands green because it reproduced something adjacent to the report.
  `Provider_Config_Name_Collision` predicted the fix would touch four name-keyed
  resolvers in `Program.cs`; the fix note says *"One thing in the report was wrong"* —
  they already keyed off the config entry. The reporter's model of the code disagreed
  with the code, and only running something would have shown it.
- A regression test nobody ever saw fail, which stops testing anything after the next
  refactor without telling you.

## The six clusters, ranked

### 1. Approval diagnosability — ~~4 files~~ **3 FIXED, 1 diagnosed, 2026-09-04**
`Denials_Do_Not_Name_The_Near_Miss` (parent) · `Approval_Bash_Glob_Does_Not_Match_Newlines` ·
`No_Match_Denial_Does_Not_Name_The_Trust_Rung` · `Trust_Rung_Denies_A_Bare_Find_With_A_Redirect`

**Top of the queue.** Not because of severity — no wrong answers — but because this
cluster *voided two experiment arms* (24 runs) rather than being debugged. It is
actively taxing the work nb exists to support. The parent report's claim is that
`approval_reason`'s five values all name the tier that refused and never the rung that
nearly matched; fixing that retires the two diagnosability children outright and turns
the third into a question instead of a repro script.

The glob/newline behaviour is arguably a real bug underneath the diagnosability one
(`approval bash *` genuinely should match a heredoc) — decide whether near-miss reporting
alone closes it, or whether the matcher changes too.

**Outcome.** Two fixes, exactly as scoped: `RegexOptions.Singleline` on approval globs
(so `*` crosses a newline), and a `Miss` channel out of `DecideBash` carrying the near
miss. Three reports closed; the fourth is diagnosed and deliberately left open.

The near-miss channel paid for itself immediately: it diagnosed
`Trust_Rung_Denies_A_Bare_Find_With_A_Redirect` on the first run, no repro script needed.
`find /etc -name hosts 2>/dev/null` classifies as **Write → /dev/null**, because
`CommandClassifier` takes the redirect target as the command's path — so trust
sandbox-checks `/dev/null` and refuses. Any command carrying `2>/dev/null` is denied under
trust for that reason, which also explains why the original arm's denial rate fell but
never reached zero. That report now names two candidate fixes; both are behaviour changes
to the trust rung, so they were not folded into a diagnosability fix.

Two things worth carrying forward:

- **The greppability rule needs pinning, not remembering.** `approval_reason` keeps the
  rung as its leading token and appends the detail in parentheses. One existing test
  asserted the reason with `Assert.Equal`; it now asserts `StartsWith` plus the detail,
  with a comment saying that *is* the contract.
- **Costumes accept the near miss and deliberately do not speak it.** Their `RefusalText`
  reproduces what the real harness says, and nb's ladder detail is not part of that. The
  detail still reaches the operator's stderr line and `approval_reason`, so fidelity and
  diagnosability did not have to trade off.

**Tests: yes, first — the best red-green candidate in the queue.** The wrong behaviour
*is a string*: `approval_reason` naming the tier that refused instead of the rung that
nearly matched. Assert the reason value and the denial text for each of the four reported
shapes (heredoc under `approval bash *`; no-match under `Trust: false`; the bare `find …
2>/dev/null` under `Trust: true` + `sandbox bwrap`). All four are cheap to observe red,
and `evals/` already asserts on model-visible strings, so the shape exists. Landing these
four *before* any fix also settles Step 1's open question for
`Trust_Rung_Denies_A_Bare_Find` — that report is stuck at "repro script written and not
yet run", so writing the failing test **is** its triage step.

### 2. Provider truthfulness — ~~1 file~~ **FIXED 2026-09-04**
`Failed_Provider_Directive_Silently_Substitutes`

One of **two open reports where nb produces a confidently wrong result** (the other is
`Image_Silently_Dropped_In_Tool_Results`, cluster 6 — they share the `provider-truthfulness`
tag for that reason). A `provider` directive that can't build a client leaves the previous
client in place, answers from a provider the program did not ask for, exits 0, and emits a
transcript with nothing in it to say so. Everything else in the queue is a denial, a
missing capability, or a diagnosability gap. Fix it regardless of cluster order: it is the
smallest fix with the worst failure mode.

**Tests: yes, first — done, and the discipline paid.** Five tests in
`nb.Tests/ProviderSubstitutionTests.cs` written before the fix. The type surface
(`ProviderUnavailableException`, `RunResult.Provider`) was added first *without* the
behaviour, so the tests failed on behaviour rather than on missing symbols — a compile
error is not an observed red. They failed with *"No exception was thrown"* and the run
returning `from-mock`: the reported bug exactly. The happy-path control passed before and
after, so the guard is not firing indiscriminately.

**Landed:** hard-fail in `ProgramEvaluator.SwapClient`, and `provider` on the result
trailer (always emitted — omitting it when it matches the config default would
reintroduce the ambiguity it removes). The REPL carve-out needed no mode flag: its catch
filter prints and continues while the program path's exits 1, so adding the new exception
to both filters gave the Notes section's requested scoping for free. `--validate` was
left alone deliberately; the gap is now documented and pinned by an eval instead.

**Not done:** effective *model* is not recorded. Nothing tracks a resolved model today,
so it needs new plumbing through the client factory. The hard-fail means the model-drop
case can no longer happen silently, so this is attribution polish, not a hole — but it is
the one part of cluster 2 still open, and it should be filed as its own item rather than
left implicit here.

### 3. Advertised schema vs dispatch path — 3 files, 1 structural fix
`Optional_Tool_Parameters_Advertised_As_Required` · `Bash_Advertises_A_Timeout_It_Ignores` ·
`Resolve_Does_Not_Show_The_Costumed_Wire_Surface`

(`Tool_Names_Diverge` was tagged into this cluster during Step 0 and moved out again in
Step 1 — see above. It is a measurement question, not a schema/dispatch disagreement.)

Step 1 narrowed this cluster twice over: `Optional_Tool_Parameters` turns out to be
**native-surface only**, and `Bash_Advertises_A_Timeout` turns out to need **three**
changes rather than one (dispatch wiring, the `required` array, and the `Math.Min`
clamp). The two overlap on a single parameter — `bash.timeout_seconds` is simultaneously
mandatory-to-send and ignored-on-arrival — so they should be fixed together or the fix
will look complete and not be.

Shared root cause: a native tool's `AIFunctionFactory` lambda supplies name+schema and
is **never invoked**; the live path hand-dispatches and reads arguments itself. Anything
expressed only in the lambda is dead code, and nothing checks the two halves agree.
`--resolve` is the same disease at the report layer — it prints canonical names, so a
costume's real wire surface is invisible exactly when you need to verify an experiment arm.

One fix shape covers all three: make the emitted schema and the dispatch path share a
declaration, and extend `ToolSurfaceGoldenTests` to assert the pair. Audit the unchecked
lambdas the timeout report names (`apply_patch`, `fetch_url`, `search_web`).

**Tests: yes, first — but as golden output, not as hand-written assertions.** The red
state is already mechanised: `nb.Tests/ToolSurfaceGoldenTests.cs` prints the emitted
schemas and is what made `Optional_Tool_Parameters` visible in the first place. Extend the
golden to cover *dispatch* as well as schema (which declared parameters does the hand-
dispatch path actually read?) and the diff is the failing test, per tool, for free.
Caveat, and the reason this cluster is *not* first in the order: the shared-declaration
fix is a design change, and a hand-written assertion about how a parameter reaches
`ExecuteAsync` is exactly the test you'd rewrite once the seam exists. Golden output
survives that; a mock-shaped unit test does not.

For `Bash_Advertises_A_Timeout_It_Ignores` specifically, one hand-written test does earn
its place, because it encodes a fact and not a design: a `timeout_seconds` *above* the
configured default must raise the timeout. That is currently impossible via
`Math.Min(requested, _defaultTimeoutSeconds)` (`BashTool.cs:86`) and stays impossible
after the dispatch wiring alone — so it is the one assertion that catches the half-fix.

### 4. Bash as a boundary — 2 files, a *decision* not a fix
`shell-tool-no-filesystem-sandbox` (live since the `ArgumentList` fix removed the
accidental control) · `Bash_Buffers_Unbounded_Output_Before_Truncating`

`plans/approval-is-not-a-boundary.md` already argues the answer: the container is the
boundary, approval is not. If that plan is accepted, the sandbox report closes as
*answered by design* with a doc change, and only the unbounded-buffer bug needs code
(stream-and-discard rather than accumulate-then-truncate — it currently leans on catching
`OutOfMemoryException`). **Blocked on a human decision, not on engineering.**

**Tests: no for the sandbox, partial for the buffer.** `shell-tool-no-filesystem-sandbox`
resolves as *accepted by design* — the behaviour does not change, so there is no red state
to observe; what it needs is the threat-model doc that plan calls work item 0, not a test.
`Bash_Buffers_Unbounded_Output` cannot be reproduced honestly at proportionate cost (the
original repro is a 20 MB/s runaway producer driven to `OutOfMemoryException`). Test the
new bounded behaviour instead — a producer emitting well past the retained head+tail
budget completes with bounded allocation — and record in the report that the test asserts
the fix rather than reproducing the bug.

### 5. Library-host correctness — 1 file
`Concurrent_Runs_Collide_On_The_Global_Console`

Two `RunAsync` calls in one process collide on Spectre's global console and one silently
never invokes the model. This is a real defect for the in-process library surface
(`Nb.RunAsync`) — the thing the downstream consumer is being pointed at. It is the same
root as the open `TODO.md` item "engine chrome still lives in nb.Core"; schedule them
together and let the output/reporter seam fix both.

**Tests: no red test — it's a race.** A test that reliably loses the race is flaky by
construction, which is why `nb.Tests/ConsoleBoundCollection.cs` currently serialises
around the bug instead of asserting it. The meaningful test arrives *with* the fix and is
not a reproduction: once the reporter seam exists, two concurrent `Nb.RunAsync` calls each
get their own sink, and you assert both invoked the client. Deleting the
`ConsoleBoundCollection` serialisation and having the suite stay green is the real signal.

### 6. Standing feature gaps — 2 files
`Image_Silently_Dropped_In_Tool_Results` · `Provider_Config_Cannot_Send_Extra_Headers`

`Provider_Config_Cannot_Send_Extra_Headers` blocks the authenticated-gateway mode (the mode
that works is the one requiring every upstream key held locally). Well-specified, pick up
cold, no urgency.

`Image_Silently_Dropped` is **misfiled as a feature gap and tagged `severity: high` /
`cluster: provider-truthfulness` instead**: `read_file` on a PNG reports success and the
model answers confidently about an image it never saw. That is the same silent-wrong-answer
shape as cluster 2, not a missing capability. Open since 2026-07-26 with candidate 1 drafted
and parked — read the **Findings (2026-07-28)** section before restarting.

**Tests: yes, first, for the image bug.** The fixture is already in the report and is
about as hermetic as tests get — an 8x8 pure-red PNG, 75 bytes. Assert that the content
sent on the wire for an OpenAI-wire provider carries the image part; today it does not,
and the model confabulates. Wire-level assertion against a fake handler, not a live
provider. For `Provider_Config_Cannot_Send_Extra_Headers` the test is ordinary
new-feature work (headers land on the request, `${VAR}` interpolates) — write it
alongside, not first; there is no wrong behaviour to pin, only an absent capability.

## Recommended order

1. ~~Step 0 hygiene + Step 1 verify-and-close~~ — both done 2026-09-04. Step 1 closed
   nothing; see above for what it changed instead.
2. ~~Cluster 2 (silent provider substitution)~~ — done 2026-09-04.
3. ~~Cluster 1 (near-miss reporting)~~ — done 2026-09-04; retired 3, diagnosed the 4th.
4. Cluster 4 decision — costs a conversation, may close 1 file for free.
5. Cluster 3 (schema/dispatch seam) — structural, do it once, guard it with the golden test.
6. Cluster 5 with the TODO console-seam item.
7. Cluster 6 on demand.
