# @-Mention File References

Status: Proposed

## Problem

Users frequently want to direct the model's attention at specific files. Today
they either type a path into prose (`"look at ConversationManager.cs"`) and
trust the model to call `read_file`, or they pre-inject content with some other
ad-hoc mechanism. The prose path works ~95% of the time but has three costs:

1. **Latency** — the model reads the file in a second turn, not the first.
2. **Reliability** — sometimes the model guesses at contents instead of reading.
3. **Typing** — the user spells out the full path by hand.

Other coding assistants (Claude Code, Cursor) have converged on `@path` as the
UX for "include this file as context." We should match that convention.

## Scope of the decision

Two layers, mostly independent:

**Layer 1 — Typing UX.** Trigger a file picker on `@` inside UglyPrompt. User
hits `@`, sees matching paths from cwd, picks one, and the literal `@path` is
inserted at the cursor. This is pure line-editor work and has no interaction
with the model.

**Layer 2 — What the model sees.** Three options, in increasing ambition:

| Option | What happens on submit | Pros | Cons |
|---|---|---|---|
| **A. Sugar only** | `@path` sent verbatim in the user message | Zero backend work. Model handles it as prose. | Still needs a `read_file` round trip. Model can still skip reading. |
| **B. Auto-expand** | `@path` replaced with a tool-result-style block containing the file content | One round trip. Guaranteed in context. | Reimplements `read_file`'s pipeline; need to decide the wire format. |
| **C. Hybrid marker** | `@path` left as-is in display, but an additional `FunctionResultContent` pre-seeded in history | Cleanest model-side shape. | Most invention; schema drift risk if read_file changes. |

Recommendation: **B** for v1. It's the smallest thing that delivers the reliability
and latency wins. Option C is nicer in theory but buys us little in practice.

## Proposed behavior

### Parsing

Scan the submitted user message for `@`-tokens matching a simple grammar:

```
@<path>[:<line-spec>]
```

Where:
- `<path>` is a relative or absolute filesystem path, ending at whitespace or end of input.
- `<line-spec>` (optional) is `N` (single line) or `N-M` (range), matching the
  `read_file` tool's existing `offset`/`limit` params.

Terminators: whitespace, newline, or end of string. No support for paths with
spaces in v1 — backslash-escaping or `@"..."` quoting can come later if anyone
actually hits this.

Non-matches (e.g., `@someone` with no corresponding file, or an email address)
are left as literal text. The token only "activates" if the resolved path
exists and passes the sandbox check.

### Path resolution

Same sandbox rules as `read_file`:
- Relative paths resolve against `shellCwd`.
- Sandbox check via `TrustSandbox.IsPathTrusted`. Paths outside cwd/temp
  prompt for approval, same as any other out-of-sandbox read.
- Symlink escapes rejected with a warning (reuse existing logic).

### Message rewriting

When the user submits `"fix the bug in @ConversationManager.cs:220-260"`, nb:

1. Adds the user's literal text as the `user` role message, unchanged.
2. Appends a synthetic `<system_reminder>`-style note listing the attached files:
   `"The user attached these files: ConversationManager.cs (lines 220-260). Contents follow."`
3. Reads each file via the existing `ReadFileTool` path (honoring line range,
   image handling, PDF extraction).
4. Adds the content as a `user` message with a clear marker — same shape as
   the existing `AddToConversationHistory` pattern for documents.

The user sees their original prose rendered. The model sees the prose plus the
attached content, already in context, no tool call needed.

### Edge cases

- **Nonexistent path** — token stays literal. No error. Rationale: `@foo` in
  prose is fine if there's no `foo` file.
- **Image files** — expand via `DataContent` same as `read_file` does today.
  The vision-capable model sees the image.
- **PDF** — existing PDF extraction path in `ReadFileTool` applies.
- **Binary file** — reject with a muted warning in the UI; leave the token literal.
- **Large file** — respect existing `read_file` size limits. If truncated,
  surface that truncation note to the model so it knows there's more.
- **Multiple `@`s** — all resolved; each becomes its own attached block.
- **Duplicate `@`s** — dedupe by resolved path + line range.
- **`@` mid-word** (e.g., `foo@bar.com`) — require whitespace or start-of-line
  before the `@` to trigger.

## UI work (Layer 1)

Inside UglyPrompt, `@` enters a transient menu mode, similar to the
existing ambient completion hints but populated from filesystem glob:

- Initial list: non-hidden entries in cwd, skipping `SkipDirectories`
  (the existing shared constant: `.git`, `node_modules`, `bin`, `obj`, etc.).
- Typing extends the filter; `/` descends into a subdirectory.
- `Tab` accepts the highlighted entry and inserts `@<path>` at the cursor.
- `Esc` or any non-candidate key cancels; the `@` stays as a literal character.

This is the largest chunk of net-new code. `FindFilesTool`'s globbing can be
reused for candidate enumeration.

## Implementation phases

### Phase 1 — Backend (Layer 2)

- Add `AtMentionParser` that scans a message for `@path[:range]` tokens.
- Wire into `ConversationManager.SendMessageAsync`: before adding the user
  message, resolve `@`-tokens, read files, build the attached-content
  user-role message.
- Reuse `ReadFileTool`, `TrustSandbox`, `FileReadTracker`.
- No UI change — users can type `@path` by hand and it'll work.

Ship this first. Provides the value; lets us validate the parser and
message shape before building UI.

### Phase 2 — UglyPrompt picker (Layer 1)

- Add `@`-triggered picker mode to UglyPrompt.
- Filesystem enumeration with the skip-directory list.
- Optional: remember recently-referenced files and float them to top.

### Phase 3 — Polish

- Line-range UI hint (type `:` after a picked file to add a range).
- Glob support (`@src/**/*.cs`) if anyone asks.
- Persist `@`-attachment history so repeated references don't re-read
  unchanged files (leverage `FileReadTracker`'s mtime tracking).

## Non-goals

- **Implicit file expansion.** Only `@`-prefixed paths expand. Bare filenames
  in prose stay as prose — the model can still `read_file` them if it wants.
- **URL expansion.** `@https://...` stays a token; fetching URLs is
  `fetch_url`'s job, explicitly gated.
- **Semantic search.** `@` picks a literal file, not "the file that contains
  X." A future `#topic` or `?query` syntax could do semantic search if we
  ever build it.

## Open questions

1. **Message shape.** Attach as a second user-role message, or splice into the
   same message with a marker? Second message is cleaner but may confuse
   providers that expect strict user/assistant alternation.

2. **Approval UX.** If `@path` points outside the sandbox, do we prompt at
   send time (blocks the turn) or just skip that token with a warning? Prompt
   probably — a silent skip is confusing.

3. **Token budget.** A user could `@` a 10k-line file and blow context.
   Enforce a per-`@` size ceiling with a warning when truncated, or trust the
   user? `read_file` has no ceiling today; consistency argues for none here
   either, but the failure mode is nastier when it happens silently at send.

## References

- `Shell/ReadFileTool.cs` — existing file-read pipeline to reuse.
- `Shell/TrustSandbox.cs` — sandbox check.
- `Shell/FileReadTracker.cs` — mtime tracking, useful for future dedupe.
- `Features/Context_Management.md` — adjacent thinking on context budget.
