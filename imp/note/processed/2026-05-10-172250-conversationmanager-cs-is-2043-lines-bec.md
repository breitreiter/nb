---
captured: 2026-05-10T17:22:50Z
repo: nb
source: cli
git-head: 2a9a582481e6
---

ConversationManager.cs is 2043 lines because streaming, tool-call merging, and history persistence all share mutable state through its fields. Tried splitting in April; broke #47 when the streaming buffer was held by another component mid-tool-merge. Don't propose carving piecewise until the streaming extraction lands.
