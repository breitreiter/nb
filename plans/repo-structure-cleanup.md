---
kind: plan
title: Consolidate project knowledge under project/ and retire the imp substrate
state: active
created: 2026-09-08
updated: 2026-09-08
---

# Consolidate project knowledge under `project/`, retire the imp substrate

nb predates the layout we now use everywhere else. Project knowledge is scattered
across four root-level dirs (`plans/`, `bugs/`, `rules/`, `TODO.md`) plus an
`imp/` substrate with its own trust model, proposal sidecar and README furniture —
and imp is no longer in use here. There is also a checked-in bug repro directory
at the root from a bug that has since been fixed.

Goal: one `project/` directory, no imp, no stray repro dir, and every in-code
cross-reference still resolving.

## Target layout

```
project/
  plans/       ← plans/            (38 files)
  bugs/        ← bugs/             (27 files)
  rules/       ← rules/            (2 + README)
  learnings/   ← distilled from imp/ (see below)
  todo.md      ← TODO.md
  README.md    ← what each dir is; the bug/plan frontmatter conventions
```

Everything else at the root stays where it is: `docs/` is user-facing spec
(README links into it), `evals/`, `prompts/`, `mcp-servers/`, `Providers/`.

### On `rules/`

The newer pattern names bugs/plans/todo/learnings and doesn't mention rules, but
this repo has two real ones (`model-policy-in-prompt-layers.md`,
`preset-floor.md`) and CLAUDE.md leans on the first from a csproj comment. A rule
has a semantics the other kinds don't — drift between rule and code is a bug, not
a note — so it earns a directory. Keep `project/rules/`, drop the imp-flavoured
`rules/README.md` and fold its one-paragraph "what is a rule" test into
`project/README.md`.

## Disposition of `imp/`

Nothing under `imp/` is load-bearing. Item by item:

| Path | Disposition |
|---|---|
| `imp/note/inbox/2026-06-03-…-gpt5x-todo-tool-phase-completeness-contract.md` | **Promote** → `project/learnings/gpt5-phase-parameter-and-todo-completeness.md`. Real, unprocessed, still actionable: the Responses-API `phase` parameter is the durable fix for the preamble-ends-turn failure our `ConversationManager` pending-todo nag fights, `TodoStatus` has no `Blocked`, and the plan-first instruction in `system.md` inverts for the Codex line. Cross-links `plans/todo-tool-capability-tiering.md`. |
| `imp/learnings/*conversationmanager*` (2 files) | **Drop.** Both describe the same thing twice, and describe it wrongly: they cite `src/ConversationManager.cs` (no such path), an "issue #47" regression that isn't ours, and history-persistence state that nb no longer has. ConversationManager is 1203 lines now, not the 2043 they assert. Gnome-generated from a single note; keeping them would plant two confident falsehoods where a future reader looks for facts. Information loss is real but negative-valued — flagging it explicitly because deletion is the human's call. |
| `imp/reference/2026-05-10-aider-s-repo-map-…` | **Drop.** It's a reference for *imp's* layer-0 repo-map, not for nb. |
| `imp/log.md` (26 lines) | **Drop.** Records substrate init and three `imp tidy` runs. No project decisions in it. |
| `imp/_meta/conventions.md`, `imp/README.md`, the per-dir `README.md`s | **Distil, then drop.** The parts worth keeping are the bug frontmatter shape (`kind/state/severity/cluster`) and the rule-vs-aspiration test, both already in use. They become `project/README.md`. Everything else is gnome trust-model and `imp note` workflow. |
| `imp/concepts/`, `imp/_index/`, `imp/note/{processed,discarded}` | **Drop.** Empty but for `.gitkeep`s and five already-processed notes. |
| `imp/_meta/config.yaml` | **Drop** with the dir. |
| `../nb.imp-proposals/` (outside the repo, gitignored, 3 pending) | **Read, then delete the dir.** Two are `plan_candidate`/`rule_candidate` from a tidy run; one proposes migrating a `Features/` doc that no longer exists. Confirm each is superseded by an existing `plans/` file before deleting; if one isn't, land its content as a plan first. |

`imp init` would re-scaffold `imp/` if anyone ran it here. Nothing schedules a
sweep against this repo (no cron entry, no user timer), so removal is stable.
The `imp` CLI itself stays installed and useful for `build` / `research` /
`health` — this retires the substrate, not the tool.

## The repro directory

`repro-trust-no-match/probe.sh` is a one-file approval-matrix probe checked in
alongside `bugs/Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md`. That bug is
**fixed (2026-09-05)** and its own text says the repro script "was never needed".

**Delete it**, and add a line to the bug's Resolution pointing at the commit that
carried it (`c7a3c93`) so it's recoverable. It doesn't belong in `evals/` — the
evals drive the Mock provider hermetically and this needs a live local model.

## Reference rewriting

125 references to `plans/…`, `bugs/…` and `rules/…` live outside those dirs:
doc comments in `nb.Core` and `nb.Tests`, section headers in `evals/run.sh`, a
comment in `nb.Core.csproj`, three in `README.md`, prose in
`docs/conversation-program-cli.md`, one in a shipped harness preamble
(`nb.Core/prompts/harness/qwen-code.md`), and eight in `CLAUDE.md`.

All are prose or comments — none is a path the code opens, and none is an
asserted string in the eval suite (`evals/run.sh:504` echoes a bug filename into
a section header, which is output, not an assertion). So a mechanical rewrite is
safe:

```
plans/  → project/plans/
bugs/   → project/bugs/
rules/  → project/rules/
```

Do it with `git grep -l` + `sed`, restricted to the three prefixes followed by a
`.md` filename, and excluding the moved dirs themselves (intra-`bugs/` links like
`[…](No_Match_Denial_….md)` are already relative and must not be touched).
Then: `dotnet build && dotnet test --no-build && ./evals/run.sh --skip-llm`.

## `.gitignore` and `todo.md`

`TODO.md` is currently **gitignored and untracked** — 366 lines of soak plan and
migration notes that have never been committed. The new pattern treats
`project/todo.md` as substrate, so my recommendation is to **track it**: it holds
the porting/migration doc outline that the downstream consumer is owed, and
that's project knowledge, not scratch. If it's ignored deliberately as a personal
scratchpad, say so and it moves as an untracked file with a `project/todo.md`
line in `.gitignore` instead.

`.gitignore` edits either way: drop `nb.imp-proposals/` and `.imp/`, replace the
`TODO.md` line with whatever we decide.

## CLAUDE.md

The "Project substrate (managed by `imp init`)" section — roughly 60 lines,
covering the human/gnome split, the proposal flow, `/imp-promote` and the
auto-approval gradient — is replaced by a short paragraph naming the four
`project/` dirs. Also delete the **Feature Documents** section: it documents a
`Features/` directory that no longer exists. The eight in-body path references
get rewritten with the rest.

The global `~/.claude/commands/imp*.md` slash commands stay — they're
machine-wide and other repos may still use imp.

## `project/constitution.md`

Worth writing here, but as a **ratification, not a founding document**. nb has
already fought most of these arguments; what it lacks is one place where the
outcomes sit. Today "what is nb allowed to become" is answered by reading
CLAUDE.md prose, two closed-as-accepted bug reports, and an accepted plan.

The admission test for a clause: **has it already settled an argument or refused
a feature?** If it hasn't, it belongs in README, not here. Candidate clauses,
each with the decision that produced it:

- **Stateless.** No history file, no per-directory state; continuity is explicit
  via `--seed`. (The composable-CLI reorientation, merged 2026-07-28.)
- **Nothing is injected.** A program gets exactly the directives it carries; the
  persona is a preset the bare-human path opts into. (`rules/preset-floor.md`.)
- **One contract, three surfaces.** File/stdin, REPL, and `Nb.RunAsync` evaluate
  the same program; the output schema is the input schema.
- **Approval records, it does not confine. The container is the boundary.**
  (`bugs/shell-tool-no-filesystem-sandbox.md` closed as accepted;
  `plans/approval-is-not-a-boundary.md` accepted 2026-09-05.)
- **Per-model behavior is data, not control flow.**
  (`rules/model-policy-in-prompt-layers.md`.)
- **`IChatClientProvider` is a versioned public contract** — an out-of-tree
  implementer exists; additions arrive defaulted.
- **MIT, so no copyleft dependencies.** (iText7 → PdfPig.)
- **Non-goals:** not a coding agent, not a chat UI, no hidden persistence, no
  filesystem sandbox.

Two things it must do that a greenfield constitution doesn't. It has to record
that **the goal itself changed once** — nb was a chat/coding agent before it was
an evaluator — so the document carries a supersession line instead of implying it
was always so. And it must stay under two pages and link *down* to `rules/` for
anything testable, or it becomes a description of the current code, which is the
failure mode for a retrofitted constitution.

Lands as its own commit between steps 2 and 3. Drafting the clause list is
mechanical (the decisions are all on disk); ratifying it is not — expect to
strike the ones that turn out to be description rather than constraint.

## Sequence

Six commits, each independently green:

1. `git rm -r repro-trust-no-match`, plus the pointer line in the bug report.
2. Promote the inbox note to `project/learnings/`; write `project/README.md`;
   read and delete `../nb.imp-proposals/`.
3. `git rm -r imp/` and the remaining `.gitignore` lines.
4. `git mv` the four sources into `project/` (`plans`, `bugs`, `rules`,
   `TODO.md` → `todo.md`). Rename-only, so history follows.
5. The `sed` reference rewrite; build + test + evals.
6. The CLAUDE.md rewrite.

Steps 4 and 5 are the ones worth reviewing as a pair — the tree is briefly
inconsistent between them, so don't stop in the middle.
