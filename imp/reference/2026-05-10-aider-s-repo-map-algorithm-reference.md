---
kind: reference
title: Aider's repo-map algorithm reference
created: 2026-05-10
updated: 2026-05-10
status: current
touches:
  files: []
  symbols: []
  features: [repo-map, tree-sitter, personalized-pagerank, token-budget]
provenance:
  author: imp-gnome
  origin: note:2026-05-10-173347-aider-s-repo-map-is-the-canonical-refere
url: https://aider.chat/2023/10/22/repomap.html
subject: Repo-map algorithm using tree-sitter and personalized PageRank
---

# Aider's repo-map algorithm reference

Aider's repo-map is the canonical reference for the tree-sitter + personalized-PageRank + token-budget shape — the algorithm we're borrowing for layer 0 of imp's substrate.

## Influence on this project

The repo-map algorithm's integration of tree-sitter parsing, personalized PageRank for relevance scoring, and token-budget constraints directly informs the design of imp's layer 0 substrate, particularly in how it prioritizes and summarizes code files based on contextual importance and size limitations.
