# Rules

Locked-in constraints. Things that, if broken, mean either there's a
bug (in code) or the rule needs deliberate revision (by us). Drift
between rules and code is an **alarm**, not a gentle suggestion.

Examples: file format specs, API contracts, design system invariants,
"never do X" prohibitions, shapes that other code depends on.

If you're unsure whether something is a rule or an aspiration: a rule
has a clear right-or-wrong test against code. An aspiration is
something we're working toward and may fall short of.

## Example shape

```yaml
---
kind: rule
title: <short rule title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
provenance:
  source: human
enforces:
  - <file glob this rule constrains>
---

# <Rule title>

<One-sentence statement of the rule.>

## Why
- <reason>
- <reason>

## How violations look
- <example of code that would violate this rule, if useful>
```

See `../_meta/conventions.md` for the full frontmatter spec and drift
semantics.
