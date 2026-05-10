# Learnings (gnome territory)

Discovered knowledge — why-decisions, post-incident reasoning,
gotchas, history. Not rules; they're awareness signals that survive
refactor and code rewrites.

The "snake in the back yard" framing: not a rule that you want a
snake there, not a guarantee one's there now, but useful to carry
forward. Learnings decay in *relevance* (the area may get rewritten),
not in *truth* (what was learned was true at the time).

## Authoring

Don't author files here directly. Instead, drop a note via
`imp note "<text>"` — the gnome distills notes into structured
learning entries on `imp tidy` and proposes the entry for review (if
cross-boundary) or commits it directly (if it lands here).

## Example shape (for reference; gnome generates)

```yaml
---
kind: learning
title: <short learning title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
status: current
touches:
  files: [path/to/file.cs]
  symbols: ["scip-symbol-id"]
  features: [<topic-slug>]
provenance:
  author: imp-gnome
relevance-horizon: YYYY-MM-DD       # optional
topics: [<topic-slug>]
verified-against:
  - { file: path/to/file.cs, hash: <12-char>, lines: 100-150 }
---

# <Learning title>

<One paragraph: what was learned, cited to source.>

**Why:** <reason this matters>
**How to apply:** <when this guidance kicks in>
```

`touches:` is the join key for `imp/_index/` lookups; `verified-against`
is what `imp tidy` checks for drift (flips `status: current` →
`status: stale` when cited code changes).

See `../_meta/conventions.md` for full conventions.
