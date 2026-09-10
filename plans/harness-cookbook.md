---
kind: plan
title: Harness cookbook — canonical solutions for the weird parts of building an nb harness
created: 2026-09-09
updated: 2026-09-09
status: proposed
state: idea
touches:
  files:
    - docs/cookbook/
    - evals/run.sh
  features: [docs, evals, oracle, approval, transcript]
provenance:
  author: claude
  note: proposed 2026-09-09 after the oracle work surfaced a third "how do I hide a file from the model" derivation in as many weeks
---

# Harness cookbook

## The problem

Building a harness on nb keeps producing the same class of question, and each time
someone — usually a coding agent — derives the answer from first principles:

- *Where do I put the answer sheet so the model doesn't grep it into context?*
- *How do I host a fake website the model has to read, in the container, without it
  turning up in `rg`?*
- *How do I know whether the model read something it wasn't given?*
- *How do I get a program into the container without it resting on disk?*
- *How do I script a multi-turn conversation so the run doesn't halt on the first
  question?*

Each has a canonical answer that follows from nb's design (the container is the
boundary; approval observes rather than confines; the program is the interface; the
transcript is the record). None of those answers is written down where the person
asking would find it. The reference docs describe *what nb does*; nothing describes
*how you build a specific thing with it*. So each session re-derives, and the
derivations diverge: one session proposed compiling the sheet into stdin, another
proposed rotating the file, a third hid it in a dot-directory. All three work. Only one
should be the default, and the next session should not have to rediscover the argument.

The audience is **mostly coding agents driving nb as a subprocess**, plus the human who
directs them. That shapes the format more than anything else: a recipe has to be
copy-runnable with exact commands and exact `jq` filters, because the reader will paste
it, not paraphrase it.

## Shape

A directory, `docs/cookbook/`, one recipe per file, plus an index. Not a section of the
CLI reference: the reference is a contract and changes with the format, the cookbook is
advice and changes with experience. Human-owned like the rest of `docs/`.

Every recipe has the same skeleton, in this order:

1. **Problem** — one paragraph, in the words someone would search for.
2. **Why it's awkward in nb** — the design fact that makes the naive approach wrong.
   Usually one of: no sandbox, no user, no state, no implicit persona.
3. **Solution** — the canonical mechanism, with a runnable example. Where a Mock-driven
   example exists, the example *is* the eval line (see *Keeping it true*).
4. **How to verify it worked** — what to assert on in the transcript, as a `jq` filter.
   A recipe without a verification step is a suggestion, not a recipe.
5. **Alternatives considered** — the other derivations, and why they are not the
   default. This is where the *argument* lives so it does not get re-had. Short.
6. **Sharp edges** — what the mechanism does not cover, stated plainly.

Frontmatter carries `touches:` like the rest of the substrate, so the gnome can flag a
recipe when the code it leans on moves. A recipe that names a directory the native tools
skip is stale the day that list changes.

## The rule for adding one

**A recipe is added when a real run needed it, not before.** The cookbook records
solutions that were actually used and actually verified, with the run that needed them
named in the recipe. Speculative recipes are how a cookbook becomes a second reference
doc nobody trusts. If a derivation happens in a session and works, that session writes
the recipe as part of finishing — the same discipline as the plan-outcome sections in
`plans/`.

## Keeping it true

The docs' worked examples already have a failure mode: they drift from the binary and
nobody notices until an agent pastes one. The cookbook is more exposed, because it is
*all* worked examples. Two mitigations:

- **Mock-runnable examples get an eval line.** If a recipe's example can run against
  the Mock provider, `evals/run.sh` runs it under a `--- Cookbook ---` heading and
  asserts the recipe's own verification filter. The recipe cites the eval name. CI then
  breaks when the recipe stops being true.
- **Live-only examples say so**, and record the model and date they were last seen
  working, the way the oracle plan records its live verification.

## Seed list

Recipes there is already a verified answer for. Each of these has been derived at least
once in a session this month; the cookbook's first job is to stop the second derivation.

| recipe | canonical mechanism | verified by |
| --- | --- | --- |
| **Hide a fixture file from discovery** (answer sheet, fake site, anything the model must not grep into context) | a dot-directory that is *also* on the native tools' skip list (`.nuget/`, `.idea/`), plus an `.ignore` at the fixture root for `rg --hidden`; keep the program that names the path out of the tree or on stdin | a session 2026-09-09 (sheet), the fake-website harness |
| **Detect that the model read something it wasn't given** | a canary entry (nonsense token no question would select) + a `jq` assertion over every `assistant_text` and every `tool_call.arguments`; reads via bash are recorded tool calls anyway | the oracle plan's step 1 live check (`zzqq` sentinel) |
| **Ship a program into a container without it resting on disk** | compile source to JSONL outside, pipe over stdin; the sheet body travels on the `oracle` event | design in `plans/oracle-resolver.md`; needs `--compile` (small, not yet built) |
| **Service a model's questions without a human** | `oracle @sheet.md`, bodies written as the full answer a user would give, `oracle_miss` as the maintenance signal | `plans/oracle-resolver.md`, live 2026-09-09 |
| **Fabricate a prior exchange the model believes happened** | `user`/`assistant` turns and JSONL tool rounds as premise; `--seed` for a captured one | CLI reference §5.7, §7; evals |
| **Script a deterministic test of a program** | the Mock provider's `MOCK:` riders (`response=`, `loop=`, `throw`, `oracle=`), one program line scripting both halves | `Providers/Mock`, `evals/run.sh` throughout |
| **Bound a runaway run** | `budget tokens` / `wall_ms` / `tool_calls` / `oracle_turns`, and what each exit reason means for a caller | CLI reference §4.4; evals |
| **Read approval as a measurement, not a control** | `denied` on the trailer, `approved`/`approval_reason` on each call; what a denial means when nothing prompts | `plans/approval-is-not-a-boundary.md` |
| **Pick the costume for a model** | one, always: `harness` in the program or `"Harness"` on the provider entry, paired with the vendor (`claude-code` ↔ Anthropic, `codex` ↔ OpenAI, `qwen-code` ↔ Qwen); a run naming none is refused. Four bare runs labelled "qwen code" cost an afternoon on 2026-09-09 | `HarnessRequiredTests`, evals "harness is required" |
| **Compare one model across two harnesses** | `harness codex` / `harness claude-code`, `--resolve` for the wire surface, diff the transcripts | README, `plans/harness-emulation.md` |
| **Host a fake website the model must read** | serve it in the container; files placed under a hidden dir as above; `approval fetch allow`; assert the fetch in the transcript | the fake-website harness (to be written up by whoever built it) |

The last row is the one that most needs writing, because it is the one least visible
from the repo.

### Seeds from the owner, 2026-09-09

These are questions harness authors actually hit. Some have a known answer and just
need writing; some are open and the recipe's first draft is the *investigation*. Several
may turn out to want a feature. **Understand the case before building anything**: a
recipe that says "here is the workaround, and here is the feature that would replace it"
is a better artefact than a flag built for a case nobody has fully described. `--compile`
is the model — it fell out of a specific argument about a specific file, and it is still
not built.

| recipe | what is known | what is open |
| --- | --- | --- |
| **Scripting prior turns vs. using the oracle** | Fabricated history is in-band, zero-code, and the baseline the oracle plan says an oracle must beat. The oracle is for when the *questions themselves* are under test, or when you cannot predict which of several asks the model will make. Rule of thumb: if you can write the user's line before the run, script it; if you can only write the *answer* and not *when it is needed*, sheet it. | A worked pair — the same task done both ways, with the transcripts side by side — so the reader sees what each perturbs. |
| **Curated internet access** | `approval search` / `approval fetch` gate `search_web` and `fetch_url`; the container's egress is the real control, and nb shares it; `plans/egress-tripwire.md` (draft) covers the outbound direction. GPT-5 has been seen **synthesising plausible documentation URLs and curling them** — the risk is not just leakage out but wrong data *in*, scoped wrong for the test. | Whether the answer is (a) no egress and a fake site in the container (the existing harness), (b) an allow-listed proxy, or (c) a recorded-response cache that replays a curated crawl. Likely all three, chosen by what the test is about. The fabricated-URL behaviour is itself a finding worth a canonical assertion: *did the model fetch anything it was not given?* — same shape as the unauthorised-read recipe. |
| **The basic container case, and podman well** | The standard deployment is one container, nb inside, one filesystem (CLAUDE.md, *nb does not confine the tools it runs*). `plans/container-bash-exec.md` records why a split design was rejected. | The actual runbook: image contents (nb binary + providers dir + fixture, nothing else), `podman run -i` with stdin as the program and stdout as the transcript, `--network none` vs. a curated network, rootless UID mapping and what it does to file ownership in the fixture, where config and keys enter (env, not baked), and how to get the transcript out. Nobody has written this down and every session reinvents the invocation. |
| **Sizing token and tool-call budgets** | The knobs and their exit reasons are documented. `budget tokens` overshoots by up to one round-trip; the dominant cost term is turns × accumulated context, not the shape of the writes (CLI reference §4.5). | A method when there is no canonical size: run unbudgeted once on the Mock or a cheap model to get the shape, then set ceilings at some multiple of the observed spend, and read `usage` on the trailer to tune. Which multiple, and whether wall-clock should be set from tokens or independently. This recipe is mostly a table of observed spends per task family, which does not exist yet. |
| **Using nb to grade an nb run** | Common because nb is already set up: pipe the transcript into a second program with `evals/judge.md` (PASS/FAIL + reason) as the system turn. `tools none` on the grader. The transcript is the same schema in and out, so the seed mechanism is the obvious carrier. | Whether the grader should see the transcript as a `--seed` (in-band, the judge "remembers" the run) or as text in a `user` turn (out-of-band, the judge reads a document). The second is almost certainly right — a judge that thinks it *was* the assistant is compromised — and the recipe should say so and show the `jq` that flattens a transcript into a readable document. Also: keep the rubric out of the graded run's container. |
| **Keeping nb's own artefacts away from the agent** | The container holds the fixture and nothing you'd mind the model reading — but nb's binary, `providers/`, `appsettings.json` (with keys), seeds and the program file are all there, and this repo's stance is that this is accepted. The hide-a-file recipe covers the sheet. | An inventory of what nb puts in reach, what each one leaks, and the cheapest mitigation for each: keys via env not file; program over stdin; seeds consumed and not left behind; providers dir readable but inert. Which of these is worth a feature (`--compile`; a `--config -` that reads config from stdin?) only becomes clear once the inventory exists. |

## What this is not

- **Not a tutorial.** Nobody is learning nb from it; they have the reference for that.
  Recipes assume the reader knows what a program is and want the specific trick.
- **Not a place for design argument.** *Alternatives considered* is three sentences,
  not a plan. If the argument is long it belongs in `plans/` and the recipe links it.
- **Not a security document.** Several recipes are about keeping things away from the
  model, and every one of them has to say, in the *Sharp edges* section, that nb is not
  a boundary and the mechanism is about avoiding accidental contamination and making
  deliberate reads visible. The word "hide" is fine; the word "secure" is not.

## Build order

1. `docs/cookbook/README.md` — the index, the skeleton, the rule for adding one, and
   the "not a security document" note. Half a page.
2. The two recipes that are hottest right now, written from the runs that needed them:
   *hide a fixture file* and *detect an unauthorised read*. Both are Mock-runnable, so
   both get eval lines. The eval for the first is the interesting one: it should prove
   `find_files`, `grep` and `list_dir` all walk past the chosen directory.
3. `--compile`, so the *ship a program over stdin* recipe is a command rather than a
   description. Same size as `--resolve`.
4. The rest of the seed list, one at a time, as sessions touch them. No batch.
5. A line in `CLAUDE.md` under Development Notes: *when a session derives a harness
   mechanism, check `docs/cookbook/` first, and write the recipe if it is not there.*

## Features this may produce

Listed so they are not built early. Each waits on its recipe being written far enough
to show the workaround is worse than the feature:

- `--compile` — parse + resolve includes + emit JSONL, run nothing. Case understood
  (the sheet argument); the smallest of these.
- `--config -` or env-only config — keys never on the container filesystem. Case not
  yet written down; wait for the artefact-inventory recipe.
- A fetch-not-given assertion, or an `approval fetch <allowlist>` — depends on which of
  the three curated-internet answers wins for the common case.

## Open questions

- Should recipes live under `docs/cookbook/` or `imp/learnings/`? They are human-owned
  advice, not gnome-distilled knowledge, and agents read `docs/` — so `docs/`. But the
  gnome should be able to propose one from a stash item, via the usual proposal route.
- Is the eval-per-recipe rule too heavy? It is the only thing that keeps a cookbook
  honest, and the cost is one `run_prog_jsonl` line per recipe. Try it on the first two
  and see.
