---
captured: 2026-05-10T17:33:47Z
repo: nb
source: cli
git-head: a64d518e45d4
---

ConversationManager.cs is 2043 lines because streaming, tool-call merging, and history persistence all share mutable state through its fields. A split attempt in April broke #47 when the streaming buffer was held by another component during a tool-merge. Don't propose carving piecewise until streaming extraction lands.
