# GPT-5x ends turn early under streaming

Status: Investigating (2026-04-24) — workaround: `git revert aba44f8` locally; testing in progress.

---

## Symptom

Mid-task abandonment with GPT-5x models: the model stops working partway through a multi-step task. The pending-todos reminder fires (`⚠ Pending todos; reminding model`) and the model still ends the turn afterward. Manually typing `continue` sometimes resumes work, but often takes multiple `continue`s before tool calls resume.

## Suspects

Both landed Thu 2026-04-23, the morning before the regression was first observed.

1. **`aba44f8` — streaming rewrite.** Main turn switched from `GetResponseAsync` to `GetStreamingResponseAsync` + `updates.ToChatResponse()` aggregation (`ConversationManager.cs:231-261`). Two plausible failure modes:
   - `ToChatResponse()` aggregation drops or mis-buckets streamed `FunctionCallContent`, so the `hasToolCalls` check at `ConversationManager.cs:264` returns false when batched would have returned true → falls into the end-of-turn branch.
   - The recursive `SendMessageInternalAsync()` after the pending-todos reminder also streams; a streamed continuation with an injected `<system_reminder>` User message produces near-empty output, matching "needs multiple `continue`s."

2. **`d06ab84` — M.E.AI 10.5 / MCP 1.2 / Anthropic SDK upgrade.** AzureFoundry switched from `GetOpenAIResponseClient(model)` to `GetResponsesClient().AsIChatClient(model)`. If gpt-5x is served through AzureFoundry, this is now hitting the Responses API via a different adapter — and the streaming + Responses-API combo is the newest path in M.E.AI 10.5.

## Reproduction

- Model: gpt-54 via (provider TBD — likely AzureFoundry).
- Trigger: any multi-step task that produces a `todo_write` plan, then runs a couple of tool calls.
- Observed: model emits a partial response, exits turn, pending-todos reminder fires, model exits again without progressing.

## Bisect plan

- [x] Revert `aba44f8` locally and re-test (in progress).
- [ ] If revert fixes it: streaming aggregation is the cause. Fix candidates:
  - Fall back to `GetResponseAsync` for Responses-API providers.
  - Build the `ChatResponse` manually from accumulated updates instead of relying on `ToChatResponse()`, to guarantee tool-call content survives.
- [ ] If revert does *not* fix it: re-test against `d06ab84^` to isolate the M.E.AI/Responses-API adapter change.
