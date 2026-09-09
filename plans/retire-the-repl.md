---
kind: plan
title: Retire the REPL
created: 2026-09-05
updated: 2026-09-09
status: current
state: done
touches:
  files:
    - Program.cs
    - FileMentionSource.cs
    - nb.csproj
    - nb.Core/Shell/TrustSandbox.cs
supersedes: []
---

# Retire the REPL

**State: done 2026-09-09.** Written 2026-09-05 on the owner's call: *"I don't think anyone is
using it as a repl any more. It's not super useful for me, and you (and other coding
agents) can't use it interactively."*

That is the whole argument and it is sufficient. nb has two execution modes and the
constituency for one of them is empty: the primary consumer stopped reaching for it, and
the other class of consumer — coding agents building test harnesses, which is what nb is
now *for* — cannot drive a TTY line editor at all. A mode nobody can use and nobody wants
is not a feature in reserve; it is a claim the documentation keeps making.

Recording the cost honestly, because it is the reason to be careful rather than quick:
the REPL is where much of this codebase was written, over many hours. Sunk hours are not
an argument for keeping it, but they are a good argument for deleting it deliberately —
in stages, with the shared machinery separated from the exclusive machinery first — rather
than pulling the thread and seeing what unravels.

## 1. What is actually exclusive to the REPL

Smaller than it feels. Measured, not estimated:

| thing | lines | notes |
|---|---|---|
| `Program.cs: RunReplAsync` | ~66 | the loop itself |
| `Program.cs: _lineEditor` + `CreateLineEditor()` | ~12 | fields/ctor at `:25`, `:34` |
| `Program.cs: runRepl` branch | ~4 | `:140`, `:205` |
| `FileMentionSource.cs` | 62 | `@`-completion **source** for the line editor |
| `nb.Tests/FileMentionSourceTests.cs` | ~120 | tests for the above |
| `UglyPrompt` 0.4.0 (`nb.csproj:17`) | — | a dependency with exactly one consumer |

Roughly **265 lines and one NuGet dependency.** The code is not the expensive part.

## 2. What must survive, and is easy to catch in the blast

This is the section to read before touching anything. Each of these *looks* like REPL
machinery and is not:

- **`@file` includes.** `ProgramParser.Parse(source, ResolveInclude)` is used by the
  **program path** too (`Program.cs:634`). What dies is tab-completion of `@` mentions in
  the line editor, not the `@file` directive syntax. `plans/At_Mention_Files.md` stays
  implemented; `plans/UglyPrompt_Multi_Source_Completions.md` (Proposed) is mooted and
  should be marked so.
- **`--output interactive`.** Independent of the REPL — a program file flips
  `interactive` → `jsonl` automatically (`Program.cs:144`), so the mode is only ever
  reached by asking for it explicitly. That is a human reading a file-run at a terminal,
  which is still a real thing. **Keep it.** What disappears is its role as the *default*
  when there is no program.
- **`NbRuntime`.** Shared: `Nb.RunAsync` builds one too (`Nb.cs:67`). CLAUDE.md currently
  describes it as "`NbRuntime` for the REPL", which will become simply wrong.
- **`ProgramEvaluator.EvaluateEventAsync`.** The REPL feeds it one directive at a time,
  but `EvaluateAsync` calls it in a loop (`ProgramEvaluator.cs:57`). It stays; whether it
  stays `public` is a separate question (see §5).
- **`MarkdownRenderer`, `UIColors` themes, the thinking spinner.** All reachable from
  `--output interactive`.
- **The "never prompts" guarantee.** `docs/conversation-program-cli.md:91` says nb never
  asks for authorization mid-run, "at the REPL or anywhere else." The guarantee survives;
  only the clause naming the REPL goes.

## 3. The consequence that is worth more than the deleted code

**The REPL is load-bearing in `plans/approval-is-not-a-boundary.md`, and removing it
changes that plan's conclusions.** This is the real reason to write this down rather than
just delete some lines.

That plan keeps `TrustSandbox` alive on a two-sided argument: the cwd+temp path rule is
"the right default for the REPL and the wrong one for a container," so Tier 2 says *keep
the code, retarget it* (§Tier 2), and open questions 2 and 3 both resolve in the REPL's
favour:

> **2. Should it be the default when nb detects a container?** Recommend: no. […] a wrong
> guess silently removes the REPL user's expected behavior.
>
> **3. Does the REPL keep the cwd default?** Recommend: yes. There is a real human
> watching, so the convenience default is right there and only there.

With no REPL there is no watching human anywhere, and the argument loses its second leg
entirely. Trust's cwd+temp scoping then protects nobody in any deployment while still
producing false denials in all of them — `bugs/Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md`
is one instance, found in a file-based eval harness, not at a REPL.

So this plan proposes, and hands to that one:

- **Retire the cwd heuristic unconditionally**, not conditionally on `boundary`. One code
  path instead of a heuristic plus a container-mode override plus tests for their
  interaction.
- **Open questions 2 and 3 dissolve.** Neither has to be answered; both existed only to
  protect the REPL user.
- **`Trust` collapses** to roughly "bump effective `MaxToolCalls`", with `approval`
  directives doing the actual authorization work. Whether the key survives at all is a
  question for that plan, not this one.

Three places currently assert the dead premise in prose and will mislead whoever picks up
the boundary work: `nb.Core/Shell/TrustSandbox.cs`'s header comment, CLAUDE.md's
`## Trust Mode` section, and §Tier 2 + open questions of the boundary plan.

## 4. Staged work

Ordered so that each stage is independently correct and the risky one is last.

1. ~~**Amend `plans/approval-is-not-a-boundary.md`**~~ — **DONE 2026-09-07** — add a Revisions entry retiring the
   REPL leg, dissolving open questions 2 and 3, and making heuristic retirement
   unconditional. Do this **first**: it is the decision that shapes unstarted code, and it
   is free to make now and expensive to unwind after the boundary work is built against
   the conditional design.
2. ~~**Correct the three prose sites** that justify trust by the REPL.~~ — **DONE
   2026-09-07**. Two sites, not three: `nb.Core/Shell/TrustSandbox.cs` and CLAUDE.md's
   `## Trust Mode`. The third (the boundary plan's Tier 2) was handled by stage 1's
   Revisions entry plus an inline pointer, which is that file's own convention. README
   needed nothing — it describes trust's behaviour without justifying it by the REPL, so
   it stays accurate until the code changes.
3. **Delete the REPL from the CLI** — `RunReplAsync`, the `runRepl` branch,
   `_lineEditor`/`CreateLineEditor`, `FileMentionSource.cs`, `FileMentionSourceTests.cs`,
   and the `UglyPrompt` package reference. Decide what `nb` with no arguments on a TTY
   does instead (see §5).
4. **Docs.** `docs/conversation-program-cli.md` loses §3 and the mode-2 bullet at `:46`,
   and the provider carve-out at `:533` loses its REPL sentence. README loses `## The
   REPL` (`:398`), the invocation line at `:126`, and the REPL clauses at `:309`/`:615`.
   CLAUDE.md loses execution mode 2 (`:102`), the `Program.cs` description's REPL clause
   (`:117`), and the development note at `:137`.
5. **Mark `plans/UglyPrompt_Multi_Source_Completions.md` superseded** by this plan.
   `plans/Terminal_Integration.md` (Implemented) needs a read — it may be wholly about the
   interactive surface.

Stages 1–2 are worth doing even if 3 never happens; they are true the moment the REPL has
no users, whether or not the code is gone.

## 5. Open questions

1. **What does bare `nb` on a TTY do?** Recommend: **print help and exit 2.** It currently
   starts the REPL, so it cannot stay silent-and-succeed. Help-and-nonzero is the
   conventional answer for a tool invoked with no work to do, and it tells a returning
   user the mode is gone rather than appearing broken.
2. **Does `--output interactive` keep its name once nothing defaults to it?** Recommend:
   yes. It describes the output shape, not a mode of operation, and renaming a published
   flag over vocabulary is not worth it (the same reasoning the boundary plan applied to
   `--trust`).
3. **Does `ProgramEvaluator.EvaluateEventAsync` stay public?** The REPL was its only
   external caller. Recommend: **yes, leave it** — it is a coherent piece of the library
   surface for a host that wants to drive directives one at a time, and
   `docs/conversation-program-api.md` should be checked before narrowing anything a
   downstream consumer may have built against.
4. **One commit or several?** Recommend: stages 1–2 in one, 3–5 in another. The first is a
   design decision, the second is a deletion; a reviewer wants those separable, and if the
   deletion is ever regretted it reverts without taking the trust simplification with it.

## 6. What this does not claim

It does not claim the REPL was a mistake. It was the authoring surface while nb was being
built, and `plans/composable-cli-reorientation.md` is the record of nb becoming something
else — a stateless evaluator that agents drive from files. The REPL is not being removed
because it was wrong; it is being removed because the program it belonged to is gone.


## Stage 1–2 outcome, 2026-09-07

Both landed; no behaviour change, no code touched beyond a comment.

**What stage 1 turned up that this plan had not anticipated.** Retiring the cwd heuristic
unconditionally is not purely a simplification: work-list item 4 of the boundary plan was
doing double duty, and it was also the *incentive* for the `boundary` directive — "you
declare because it helps." Remove the heuristic everywhere and `boundary container` no
longer buys a behavioural reward, which risks a declarative directive decaying into
ceremony. The revision names that cost rather than hiding it, and resolves it: the
directive stays declarative, earning its place through the transcript record (grill #6
puts `boundary:` in `--resolve` and the trailer) with item 5's startup warning as the
forcing function instead of a reward. Keeping a rule that protects nobody just so the
directive has something to switch off would be the worse trade.

**One correction to my own earlier claim.** I had said in conversation that removing the
path rule collapses `Trust` to "roughly bump `MaxToolCalls`." Wrong:
`ApprovalPolicy.IsBashCommandTrusted` filters by category, sits behind the caller's danger
check, and trusts a `Run` command with no extractable path before any path check runs.
Trust remains a real implicit-grant rung; it loses the path scoping, not its purpose.

Stage 3 (the deletion) is unchanged and still proposed.

## Stages 3–5 outcome, 2026-09-09

Landed as planned. Deleted: `RunReplAsync`, the `runRepl` branch, `_lineEditor` /
`CreateLineEditor`, `FileMentionSource.cs`, `FileMentionSourceTests.cs`, and the
`UglyPrompt` package reference. Bare `nb` on a TTY prints help and exits **2** (open
question 1's recommendation, taken as written). Open questions 2 and 3 were left alone:
`--output interactive` keeps its name and `EvaluateEventAsync` stays public.

`dotnet test` 615/615 and `evals/run.sh --skip-llm` 76/76 — neither suite referenced the
REPL, so the deletion needed no test changes and produced no regression to catch. Stage
2's earlier work meant nothing in `TrustSandbox` or `ApprovalPolicy` had to move.

**Two things this plan under-counted.**

*Docs were more than a delete.* Removing §3 from `docs/conversation-program-cli.md`
renumbered §4–§11 down one, and the plan had not noticed the `### 5.x` subsection
headers or the nine `§n` cross-references that ride along. Renumbering also exposed a
pre-existing off-by-one: the flag table's `--seed`/`--config`/`jsonl` pointers and the
"two models in sequence" pointer had each been one section high since before this work.
Corrected in passing — they now name Seeds, Configuration resolution, the JSONL wire
format and Worked examples respectively.

*`plans/Terminal_Integration.md` needed no change.* Stage 5 flagged it for a read on the
suspicion it was wholly about the interactive surface. It is not — it is the design doc
for the bash tool and the environment block, both of which are untouched by the REPL's
removal. Left as Implemented.
