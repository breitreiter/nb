---
kind: plan
title: Todo-tool capability tiering — A/B test before committing
created: 2026-06-03
updated: 2026-06-03
status: current
state: exploring
touches:
  files:
    - TodoTool.cs
    - TodoManager.cs
    - ConversationManager.cs
    - system.md
    - system.LocalLlm.qwen2-5-coder-32b.md
    - system.LocalLlm.qwen3-30b-a3b-instruct-2507-ud-q6-k-xl.md
  features: [todo-tool]
provenance:
  author: human
---

# Todo-tool capability tiering — measure before it fans out

## Why this plan exists

nb is the proving ground for ~4 downstream projects. The todo tool's
behavioral steering is currently one-size-fits-all and implicitly tuned
for capable models. Before we change it, we want **evidence** that the
change helps weak local models without regressing strong ones — because
whatever we pick here gets duplicated.

Source material: OpenAI GPT-5.x guidance digest (`/tmp/gpt-todo-tool.md`,
captured as imp note `2026-06-03-150106-gpt5x-todo-tool-phase-completeness-contract`).

## Current behavior (the control, "V0")

- Todo tools registered unconditionally for every model
  (`ConversationManager.cs:239-240`).
- ~40-line prescriptive `todo_write` description sent identically to all
  models (`TodoTool.cs:21-58`), including "write the checklist FIRST".
- Plan-first nudge also in base prompt (`system.md:51`).
- Forced-continuation reminder loop (`ConversationManager.cs:787-802`):
  when a turn would end with active todos, inject a `<system_reminder>`
  and force another turn. Deduped only on **set-change** — no hard cap.
- Status set: Pending / InProgress / Completed / Cancelled. **No Blocked
  terminal state.** When blocked, the tool tells the model to keep the
  task `in_progress` and add a blocker task (`TodoTool.cs:54`) — which
  can never satisfy the stop-condition, risking a reminder loop.

## Hypothesis

V0 is fine-to-good for Sonnet, and a net drag on weak local models
(Qwen-coder-32B, Qwen3-30B), via two failure signatures:

- **stop-after-plan** — model emits the todo list as its answer and ends
  the turn without executing (the documented Codex plan-first inversion).
- **reminder-loop** — model fails to mark items complete / churns the
  active set, so the reminder re-fires and burns toward MaxToolCalls.

## Candidate fix ("V-fix") being validated

1. **Add `Blocked` terminal state + reason.** New `TodoStatus.Blocked`,
   optional `Reason` on `TodoChange`/`Todo`, surfaced in `Render()`,
   excluded from `GetActive()`. Reword `TodoTool.cs:54` to mark blocked
   instead of holding `in_progress`.
2. **Slim, neutral tool description.** Strip behavioral coaching
   (plan-first, when/when-not) from the global `todo_write` description;
   keep only mechanism (statuses, partial-update semantics).
3. **Move plan-first pressure into the per-model prompt layer.** Present
   for Sonnet / mainline (`system.md` or provider layer); absent or
   inverted for weak/Codex-line models via
   `system.LocalLlm.<slug>.md` (the layering already exists,
   `Program.cs:386-394`).
4. **Bound the reminder.** Add a hard cap of N fires per user turn
   (propose N=2) on top of the existing set-change dedup, so a weak
   model can't loop into MaxToolCalls. Provider-agnostic; strong models
   rarely hit it.

Boundary decision already settled (see imp note): the `Blocked` state and
completeness contract stay **provider-agnostic** in shared code. Provider-
unique turn mechanisms (OpenAI `phase` / `previous_response_id`) are NOT
lifted into the shared layer — if ever needed they stay inside a plugin's
`IChatClient`. Don't widen `IChatClientProvider`.

## Why GLM is not a valid proxy (decision recorded)

The Qwen host is currently running GLM. GLM is rejected as a stand-in for
this test because:
- **Different family** — GLM (Zhipu) has its own tool-use training; todo
  behavior doesn't transfer to Qwen.
- **Capability tier is the variable under test** — GLM self-hosts span
  GLM-4-9B to frontier-class GLM-4.6. A large GLM behaves like the
  capable tier and would falsely reassure us.

GLM *is* worth an opportunistic run as a **mechanism-safety** check on a
third family (does V-fix's bounded reminder / neutral description behave),
but its results must be labeled mechanism-only, never tier-evidence.

## Test design

### Models (run on the actual hardware)
- **Sonnet** (Anthropic provider) — strong-tier reference.
- **Qwen2.5-Coder-32B** (`system.LocalLlm.qwen2-5-coder-32b.md`).
- **Qwen3-30B-A3B-Instruct-2507** (the configured default,
  `appsettings`: `Model: Qwen3-30B-A3B-Instruct-2507-UD-Q6_K_XL`).
- *(optional)* GLM currently loaded — mechanism-safety only.

### Variants
- **V0** = current `master` build.
- **V-fix** = the four changes above. Implement behind an env toggle
  (e.g. `NB_TODO_EXPERIMENT=fix`) so a single build A/Bs cleanly with no
  rebuild confound; the toggle gates description text, reminder cap, and
  Blocked registration.

### Task (controlled, exercises all signatures)
Run single-shot in a scratch dir with a 4-item spec where item 4 is
genuinely blockable, e.g.:

> "Build a small CLI in this folder with four features: (1) a `greet`
> command, (2) a `--upper` flag, (3) a `--count N` repeat flag, and
> (4) a `fetch` command that pulls today's rate from
> `http://127.0.0.1:9/rate` (unreachable). Implement everything you can."

Items 1-3 are completable; item 4 forces a Blocked-vs-loop decision.
Keep the prompt, scratch dir, and seed identical across runs. 3 trials
per (model × variant) cell to see variance.

### Metrics (capture per run)
1. Created todos before executing? (todo_write called first)
2. **stop-after-plan**: turn ended right after the plan, zero execution? (y/n)
3. Reminder fires: count of "⚠ Pending todos; reminding model" lines.
4. **reminder-loop**: hit MaxToolCalls or churned with no net progress? (y/n)
5. Terminal reconciliation: at end, any item left pending/in_progress? (count)
6. Blocked usage (V-fix): item 4 marked `blocked` w/ reason vs looped? 
7. Task correctness: features 1-3 actually work? (manual check)
8. Cost proxy: total tool calls, wall time.

### Decision criteria
- Ship **bounded reminder + slim description** if V-fix reduces signatures
  3/4/5 on the weak models with **no regression** on Sonnet (1,5,7 hold).
  These are low-risk; lean toward shipping even on modest signal.
- Ship **per-model plan-first gating** if V0 shows stop-after-plan on weak
  models AND removing plan-first reduces it without hurting Sonnet's
  task correctness.
- Ship **Blocked state** if it converts item-4 reminder-loops into clean
  blocked-terminals on at least one weak model (helps the worst case;
  neutral elsewhere). Watch metric 6 for weak models failing to emit
  `blocked` — if they can't, Blocked helps less than hoped, log it.

## Turnkey run procedure (at the server)

1. `git checkout master && dotnet build` → V0 binary.
2. Implement V-fix behind `NB_TODO_EXPERIMENT=fix` on a branch; build.
3. For each model: set `ActiveProvider` + `Model` in appsettings; confirm
   the matching `system.LocalLlm.<slug>.md` resolves
   (slug = `SlugifyModelName(Model)`, `Program.cs:184`).
4. From `bin/.../net10.0`, run the task single-shot, fresh scratch dir
   and cleared `.nb_conversation_history.json` each trial:
   `./nb "<task prompt above>"`
5. Record the 8 metrics. 3 trials per cell.
6. Optional GLM mechanism-safety pass (label results mechanism-only).
7. Fill the results table below; apply decision criteria.

## Results (to fill in)

| Model | Variant | created-first | stop-after-plan | reminder fires | loop | left-active | blocked-ok | task-ok | tool calls |
|---|---|---|---|---|---|---|---|---|---|
| Sonnet | V0 | | | | | | n/a | | |
| Sonnet | V-fix | | | | | | | | |
| Qwen2.5-Coder-32B | V0 | | | | | | n/a | | |
| Qwen2.5-Coder-32B | V-fix | | | | | | | | |
| Qwen3-30B | V0 | | | | | | n/a | | |
| Qwen3-30B | V-fix | | | | | | | | |
| GLM (mechanism-only) | V-fix | | | | | | | | |

## After the test
- Distill the outcome into an imp learning (capability-tiering rule for
  todo tools) so the decision — not just the code — fans out to the other
  projects.
