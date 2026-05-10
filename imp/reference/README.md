# References (gnome territory)

Archived external sources — papers, blog posts, third-party docs,
anything that influenced project direction. Each reference includes
both the URL and a locally-archived snippet so it survives link rot.

## Authoring

Don't author files here directly. Drop a note that includes a URL
via `imp note`. The gnome fetches the page, archives a snippet
locally, submits to the Wayback Machine, and generates a structured
reference entry on the next `imp tidy`.

```
imp note "salience model in the storylet engine is from Emily Short:
https://emshort.blog/2016/04/12/standard-patterns-in-choice-based-games/"
```

## Example shape (for reference; gnome generates)

```yaml
---
kind: reference
title: <short title>
created: YYYY-MM-DD
updated: YYYY-MM-DD
status: current
touches:
  files: [path/to/code.cs]
  features: [<topic-slug>]
provenance:
  author: imp-gnome
subject: <what external thing this references>
url: https://...
archived-at: _archive/<slug>.md
wayback-url: https://web.archive.org/web/...
---

# <Reference title>

<Plain description of what this is, what we use it for.>

## Influence on this project
<Where this shows up in the code or design.>

## Archived snippet
See `_archive/<slug>.md` for the locally-saved page content.
```

See `../_meta/conventions.md` for full conventions.
