---
kind: learning
title: Streaming state complexity in ConversationManager
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: [src/ConversationManager.cs]
  symbols: [ConversationManager]
  features: [streaming, tool-call-merging, history-persistence]
provenance:
  author: imp-gnome
  origin: note:cli
---
# Streaming state complexity in ConversationManager

The ConversationManager.cs file is 2043 lines long because streaming, tool-call merging, and history persistence all share mutable state through its fields, making it unsafe to split without first extracting streaming logic. A prior attempt to split the class in April broke issue #47 when the streaming buffer was held by another component during a tool-merge operation.

**Why:** The shared mutable state across key features creates coupling that can lead to race conditions and broken behavior if not carefully coordinated.

**How to apply:** Do not attempt to refactor or split ConversationManager piecemeal until the streaming extraction is complete and the streaming buffer is no longer managed through shared mutable state.
