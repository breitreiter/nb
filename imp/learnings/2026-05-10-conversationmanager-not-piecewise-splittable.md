---
kind: learning
title: ConversationManager not piecewise-splittable
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [ConversationManager.cs]
  symbols: [ConversationManager]
  features: [streaming, tool-call-merging, history-persistence, state-management]
provenance:
  author: imp-gnome
  origin: note:2026-05-10-173347-conversationmanager-cs-is-2043-lines-bec
---

# ConversationManager not piecewise-splittable

ConversationManager.cs is 2043 lines long because streaming, tool-call merging, and history persistence all share mutable state through its fields, creating tight coupling that prevents safe piecewise splitting. A prior attempt to split the component in April caused regression #47 when the streaming buffer was held by another component during a tool-merge operation. 

**Why:** The shared mutable state across streaming, tool-merge, and history-persistence logic creates non-local dependencies that are difficult to isolate without risking race conditions or inconsistent state during concurrent operations.

**How to apply:** Do not attempt to carve out pieces of ConversationManager until the streaming extraction is complete, as the current state of streaming integration makes any structural changes fragile and high-risk.
