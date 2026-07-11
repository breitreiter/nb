---
captured: 2026-06-03T15:01:06Z
repo: nb
source: cli
git-head: d358c1c4f87f
---

Source: OpenAI GPT-5.x guidance digest (saved at /tmp/gpt-todo-tool.md). Relevant to our session todo tool (TodoTool.cs / TodoManager.cs) and the pending-todo stop-condition in ConversationManager.cs.

The GPT-5 "model emits a preamble/progress note then ends the turn as if done" failure — the thing our pending-todo reminder was built to fight — has a real API-level fix now, not just a prompt one:

1. `phase` parameter (Responses API, introduced GPT-5.4, carried into GPT-5.5): tag assistant items `phase: "commentary"` for intermediate updates vs `phase: "final_answer"` for the completed answer. Missing/dropped `phase` "can cause preambles to be interpreted as final answers and degrade behavior" on multi-step tasks. If you replay assistant items into the next request manually (instead of `previous_response_id`), you MUST round-trip the original `phase` values verbatim or you reintroduce the bug. This is the real lever; the `<persistence>` reminder hack is now a fallback, not load-bearing.

2. Completeness contract still recommended for long-horizon work: treat the task as incomplete until all items are covered or explicitly marked `[blocked]`; keep an internal checklist; reconcile every TODO at closure as Done / Blocked (one-sentence reason) / Cancelled; never end with `in_progress` or `pending`. This is the structured, state-gated version of our reminder nag.

3. Codex-tuned line (GPT-5.3-Codex): advice INVERTS — remove upfront-plan and preamble prompting, because for those models it can cause the model to stop abruptly before the rollout completes. A todo-tool shape that forces an early plan-emit step is counterproductive there.

Implications for our todo tool:
- We have no `Blocked` status and no reason field — TodoStatus is only Pending/InProgress/Completed/Cancelled. The contract wants Blocked(+reason) as a legitimate terminal state distinct from Cancelled.
- Our ConversationManager pending-todo reminder is exactly the prompt-level "keep working" nag the guidance now classes as a fallback. The durable fix is provider-level (phase / previous_response_id), which lives in the provider plugins, not the todo tool.
- Our system.md tells the model to "write the checklist FIRST, then execute" — that's the plan-emit-early pattern that HELPS mainline models but HURTS the Codex line. Given we already do model-specific prompts, this guidance should be gated by model family rather than unconditional.
