---
kind: plan
title: egress tripwire — halting a run that tries to publish a marked string
created: 2026-08-12
updated: 2026-08-12
status: current
state: draft
touches:
  files:
    - Shell/EgressGuard.cs
    - Shell/SecretScanner.cs
    - Transcript/ProgramParser.cs
    - Transcript/TranscriptEvent.cs
    - Transcript/ExitReasons.cs
    - ConversationManager.cs
    - Facade/NbRuntime.cs
  features: [tool-surface, approval, transcript, diagnostics]
provenance:
  author: claude
---

# egress tripwire — halting a run that tries to publish a marked string

## Context

nb is used as an eval harness, which means programs deliberately mislead the model:
fabricated history, fake tools, planted bait, contradictory instructions. That is the
point of the instrument and nothing here should make it harder.

The cost of that posture is that a confused agent, mid-run, may try to send something
off the box — most plausibly a credential it found in the environment or a file, pasted
into a search query or a URL because that seemed like the next step. Not an attack: a
mistake, of the kind the harness is specifically engineered to provoke.

So the ask is a tripwire, not a security boundary: the program author names strings that
must not leave, and the run halts if the model tries to send one.

**This plan is not a defense against a model that wants to exfiltrate.** No encoding
analysis, no steganography, no covert-channel reasoning. A model that base64s a key past
the filter wins, and that is an accepted, explicit limitation. The threat model is
*carelessness with a marked string*, and the deliverable is an observation — "at turn 7
the model put the planted credential into a search query" — that shows up in the
transcript and in `$?`.

## Pushback, before the design

Four things worth settling first, because they change what gets built.

### 1. Agreed on bash — but the reason matters, and there is already a better answer

Not intercepting `curl` is correct. A regex tripwire over shell commands is nearly all
false positives in the one case that matters: `curl -H "Authorization: Bearer $TOKEN"` is
what a correct agent does, and a filter that halts on it makes the harness unusable for
any program with a real API in it.

But that reasoning only justifies *not regex-scanning* bash. It does not make bash a safe
channel — bash is by an order of magnitude the widest egress path nb has, and it is the
one a confused model reaches for most.

nb already has the right tool for it, and it is strictly better than pattern matching:
`approval sandbox bwrap` runs the bash child under bubblewrap **with no network** (see
`ApprovalPolicy.Sandbox` / `BwrapSandbox`; `bwrap-net` opts network back in). Containment
beats detection — it needs no patterns, has no false-positive surface, and cannot be
encoded around.

So the recommendation is a division of labor, and the docs should state it in one line:

- **bash egress** → contained by `approval sandbox bwrap`. Not scanned.
- **nb's own network tools and MCP** → scanned by the tripwire, because those have
  structured arguments and no isolation story.

A program that cares about egress and leaves bash netted is choosing that. An opt-in
`egress scan +bash` exists (below) for authors who want the noisy version anyway, off by
default.

### 2. MCP is the gap you didn't mention, and it is the best fit for this mechanism

A remote (`http`) MCP server is an egress channel by construction, and a stdio server can
be a proxy for one — a Slack or GitHub or issue-tracker MCP takes a string from the model
and publishes it. Meanwhile MCP tool arguments are *structured*: discrete named string
values, small, with none of the `Authorization:`-header ambiguity that makes bash
unscannable.

That is the ideal tripwire surface. **MCP tool arguments should be scanned by default**,
alongside `fetch_url` and `search_web`. Scanning only the two nb-native web tools would
leave the obvious hole open.

(No attempt to distinguish stdio from http transports. A leak into a local stdio server
that forwards it is still a leak, and the author controls the patterns.)

### 3. Be honest about what is already gone

Everything in the model's context has already been transmitted — to the provider — before
any tool call happens. If a program plants a credential and the model merely *says* it in
an assistant message, it has left the machine, and no tripwire on tool arguments will fire.

That is inherent to running a hosted model and is not a flaw to fix here, but the feature
must be documented as **"the model tried to publish this to a third party of its own
choosing,"** not "the secret stayed on this box." The first is a true and useful claim.
The second is false, and a user who believes it will make worse decisions than one with no
tripwire at all.

For programs where this actually matters, the mitigation is a local provider — and the
docs line should say so.

### 4. Canaries, not credential-shaped regexes

The natural first instinct is a generic blacklist: `sk-ant-`, `AKIA[A-Z2-7]{16}`, the rest
of what `SecretScanner` already carries. Available as a preset, and it is the wrong
default idiom.

The high-value pattern for an eval is: **the program plants a distinctive fake secret and
blacklists that exact string.** Zero false positives, and it tests exactly the hypothesis
the eval is about — did this model, under this pressure, hand over the thing we placed in
front of it. Generic patterns fire on unrelated strings the model read out of a real repo
and tell you nothing about the model.

The docs should lead with the canary idiom and mention the preset second.

## Design

### The directive

```
egress deny <regex>          # halt if a scanned argument matches (repeatable)
egress deny-env <VAR>        # halt if a scanned argument contains the literal value of $VAR
egress deny preset:secrets   # the SecretScanner pattern set
egress scan +bash -mcp       # adjust the scanned channel set
```

Follows the existing `<verb> <key> <value>` shape of `approval` and `budget`, and parses
through the same `ApprovalEvent`-style path. Content runs to the end of the logical line,
so a regex may contain spaces and needs no quoting. Patterns accumulate; there is no
`egress none` — a tripwire a later line can silently disarm is worse than no tripwire.

Default scanned channels: `search_web`, `fetch_url`, all MCP tools. Not scanned: `bash`,
file tools, fake tools, `nb_*` resource tools.

`deny-env` is the one that protects a real machine rather than a planted bait: nb runs
with live provider keys in its environment, and `egress deny-env ANTHROPIC_API_KEY` reads
that value once at run start and matches it as a literal (`Regex.Escape`). The value must
never reach the transcript, the console, or an error message — only the variable name is
ever printed. An unset variable is a startup error, not a silent no-op; a silently empty
pattern would match nothing while reading as protection.

### Config layer

```jsonc
"//Egress": "Optional egress tripwire. Deny: regexes; DenyEnv: env var names matched as literals; Scan: channels ('search_web','fetch_url','mcp','bash'). Program `egress` directives add to these and cannot remove them.",
"Egress": {
  "Deny": [],
  "DenyEnv": [],
  "Scan": ["search_web", "fetch_url", "mcp"]
}
```

Program directives layer *on top* and are additive only, mirroring how `approval` layers
onto config. An operator-set pattern is a floor a program cannot lower.

### Matching

One new file, `nb.Core/Shell/EgressGuard.cs` — a small class, no interface.

For a scanned call, walk the argument tree and collect every string-valued leaf (recursing
through nested objects and arrays). Match each leaf independently rather than matching the
serialized JSON blob: JSON escaping (`\"`, `\n`, `\uXXXX`) would otherwise both create
false negatives and make the reported offset meaningless.

Each leaf is tested twice: as-is, and URL-decoded. URL-encoding is the single
transformation that occurs *without* the model trying to evade anything — these are URLs
and query strings, and encoding is the correct behavior for the tool. One extra
`Uri.UnescapeDataString` closes the only accidental bypass that matters. Base64 is
deliberately not decoded: it does not happen by accident, so catching it is
counter-surveillance, which this feature explicitly is not.

Regexes compile at program-load time with `RegexOptions.None` (case-sensitive — secrets
are) and a per-match timeout of ~200 ms. A malformed pattern fails `--validate` with the
offending line number, never at turn 7 of a long run.

### Halting

Order matters, and it is chosen so the *intent* survives — the same reasoning as
`plans/web-search.md`, where a search that cannot execute must still be recorded:

1. The `ToolCallEvent` is emitted normally. The attempt is the finding; it must be in the
   transcript with full arguments.
2. The guard runs **before** the approval decision and before execution. The call does not
   run — not even if it would otherwise have auto-approved.
3. A `ToolResultEvent` records the block: which pattern name fired, which argument path,
   and a **redacted** excerpt (first/last 4 characters). Transcripts get shipped around;
   this one must not become a fresh copy of the secret. A `deny-env` hit reports the
   variable name and nothing else.
4. The run halts. The model does not get the result back and does not get another turn.

New `ExitReasons.EgressTripwire = "egress_tripwire"`. It needs its own process exit code:
an eval harness distinguishing "the model tried to leak the canary" from "the run ran out
of budget" is the entire point of the feature, and folding it into 3 (aborted) destroys
that signal at the shell boundary. Codes 0–4 are taken and 5 is retired-and-poisoned, so
**exit code 6**.

### What is deliberately not built

- No non-halting `warn` disposition in v1. It is defensible — for an eval you often want
  to watch what the model does *after* the attempt — but it is a different feature with a
  different transcript contract, and "halts the run" is what was asked for. The event
  shape leaves room for it.
- No scanning of assistant text, file writes, or the provider request. See pushback #3.
- No encoding, entropy, or similarity analysis.

## Phases

1. **`EgressGuard` + patterns.** The class, leaf-walking, URL-decode pass, redaction,
   `preset:secrets` wired to the existing `SecretScanner` patterns. Unit tests: match,
   no-match, nested arg, URL-encoded, redaction never emits the full value.
2. **Config + `deny-env`.** `Egress` block in `ConfigurationService`, `appsettings.example.json`
   in sync, env resolution at startup with a hard error on unset.
3. **Directive + validation.** `EgressEvent` in the schema, `ProgramParser` case,
   evaluator wiring, regex compilation at load, `--validate` coverage for bad regexes and
   unknown channel names.
4. **Enforcement + exit.** Guard call sites for `search_web`, `fetch_url`, MCP dispatch in
   `ConversationManager`; the block result event; `ExitReasons.EgressTripwire` → 6.
5. **Opt-in bash channel.** `egress scan +bash` over the `command` argument, off by
   default, documented as noisy and as second-best to `approval sandbox bwrap`.
6. **Docs.** `docs/conversation-program-cli.md` directive reference; the honest-limits
   paragraph (pushback #3); the canary idiom as the lead example; the
   bash-is-contained-not-scanned line.

## Open questions

- Should a tripwire hit be *recoverable* in the REPL — i.e. does the long-lived
  `ProgramEvaluator` stay usable after a halt, or is the session finished? Halting a
  file/stdin run is unambiguous; the REPL is an authoring surface where a fired tripwire is
  usually a program bug you want to fix and re-run in place. Leaning: report and refuse the
  turn, keep the session.
- Is `preset:secrets` worth shipping at all given pushback #4, or does its existence make
  the low-value idiom look like the recommended one?
