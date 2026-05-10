# nb substrate

Structured project knowledge for this repo, organized as a four-layer
stack. This `imp/` dir holds gnome-maintained content; root-level
`plans/`, `bugs/`, `TODO.md`, `rules/` are human-owned.

Full conventions: `_meta/conventions.md`. Canonical design rationale
is in imp's source repo at `project/substrate-layers.md`.

## Layout (this dir)

- `learnings/` — discovered knowledge, why-decisions, gotchas.
- `reference/` — archived external sources (URLs + local snippets).
- `concepts/` — narrative synthesis pages (auto-generated, selective).
- `_index/` — per-file/symbol/feature lookups (auto-generated).
- `note/inbox/` — write target for `imp note`. The gnome processes
  these into structured entries on the next `imp tidy`.
- `log.md` — append-only chronological history.

## Layout (repo root, human-owned)

- `plans/` — design intent, specs, in-flight work.
- `bugs/` — bug reports.
- `TODO.md` — running todo list.
- `rules/` — hard project invariants (substrate-shaped, human-authored).

## Trust model

- **You (human)** and **foreground Claude** — full read/write
  everywhere.
- **imp (gnome)** — writes directly to `imp/*` under a distinct git
  author (`imp-gnome <noreply@imp.local>`). For cross-boundary
  changes (`rules/`, `plans/`, `bugs/`, `TODO.md`), imp produces
  proposals at `nb.imp-proposals/` for review via
  `/imp-promote`.

See `_meta/conventions.md` for the kinds taxonomy, drift semantics,
and the auto-approval gradient.
