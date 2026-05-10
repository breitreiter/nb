# Task Handoff

Status: Planned

## Problem

Long tasks accumulate context that becomes noise. The model carries the full history of
exploration, dead ends, and incremental decisions into the implementation phase — where
that context mostly just burns tokens and increases distraction.

For Azure/Foundry reasoning models this matters more: they do better with a clean,
structured brief than with a long conversational history.

## Solution

Two commands that support an explicit planner → builder → reviewer cycle. No sub-agents,
no new processes — just structured context resets with a handoff document in between.

## Commands

### `/handoff [filename]`

Sends a prompt asking the model to write a continuation packet to `filename`
(default: `handoff.md`) using the standard format (see below). The model uses
`write_file` like any other task.

Does **not** clear context automatically — the user reviews and optionally edits
the packet before resuming.

### `/resume [filename] [role?]`

1. Reads the packet file
2. Clears conversation history
3. Injects the packet as the opening user message, with role-specific framing appended
4. Displays: `Resumed as builder — context cleared.`

Role defaults to the `NextRole:` field in the packet. Can be overridden:
`/resume handoff.md reviewer`

## Packet Format

Written by the model, in Markdown:

```markdown
# Task Handoff
NextRole: builder

## Goal
One sentence describing what we're trying to accomplish.

## Plan
1. Step one
2. Step two

## Completed
- What's been done, with relevant file paths and decisions.

## Remaining
- Next actions, in order.

## Technical Context
Decisions, gotchas, and paths the next session needs.
```

## Role Framing

**Claude**: trusted to infer appropriate behavior from the packet content and the
`NextRole` field. No additional framing injected.

**Azure/Foundry**: explicit role framing appended to the injected packet, because
these models respond better to structured instruction than to inference:

| Role | Appended instruction |
|------|----------------------|
| `planner` | "Your role is to plan. Produce a detailed, step-by-step implementation plan. Do not start building. Write the result as a handoff packet when complete." |
| `builder` | "Your role is to build. Implement the plan exactly as specified. Note any necessary deviations inline. Do not re-plan. Use /handoff when done." |
| `reviewer` | "Your role is to review. Evaluate whether the implementation achieves the goal — check completeness, correctness, and quality. Report findings clearly." |

Role framing is only injected for providers whose name contains "Azure" (configurable).

## Prompt sent by `/handoff`

> We're handing off to a fresh session. Write a continuation packet to `[filename]`
> using the standard handoff format. Be specific about what's been completed and
> what remains — the next session has no other context.

## Implementation Notes

- Both commands live in `CommandProcessor.cs`
- `/resume` uses existing `ClearConversationHistory()` and `AddToConversationHistory()`
- Role framing selection is driven by the active provider name (already available in `ConversationManager`)
- No new tools, no new files beyond the packet itself
