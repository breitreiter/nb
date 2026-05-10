---
captured: 2026-05-10T17:50:50Z
repo: nb
source: cli
git-head: 783855980534
---

add streaming-output support as a new plan. The GPT-5 truncation issue is the trigger; current ConversationManager state-coupling means we need a proper extraction pass before we can reliably stream. Open question: do we extract first then add features, or carve a streaming-only path alongside?
