---
kind: bug
title: '`approval bash *` allows only single-line commands, so a heredoc is denied'
created: 2026-09-04
updated: 2026-09-04
status: current
state: fixed
severity: medium
cluster: approval-diagnosability
---

# `approval bash *` allows only single-line commands, so a heredoc is denied

Status: **Fixed 2026-09-04** — suggestion 1, the one you ranked first. Originally: Open (2026-09-04) — found while building a documentation-retrieval eval
harness that hands an agent a markdown tree and asks a question, one nb call per
question. Measured against a published `nb-publish` build outside this tree,
provider `LocalCoder`/`qwen3-coder-next`.

**This voided an arm of 12 runs.**

## Symptom

```
approval default deny
approval bash *
```

reads as *"auto-approve every bash command."* It auto-approves every bash command
**that fits on one line**, because a glob's `*` does not match a newline.

Three denials in a single run, all `approval_reason: "default-deny"`, all on a
**33-line** command. The model was computing a set difference in Python — the right
method for the question — and wrote it the way anyone would:

```
python3 << 'EOF'
import csv
from io import StringIO
…32 more lines…
```

The run ended `exit_reason: approval_denied`.

Evidence: `build/eval/fs-a1/q03__r1/transcript.jsonl`, in a private harness repo
outside this tree — three `tool_call` events with `approved: "deny"` and
`arguments.command` containing 32 newlines. Program alongside it at `program.nb`.

## Why the output is not wrong, exactly

The docs describe whole-string matching accurately, and the rationale is clearly
right:

> Matched against the **whole command string**, not the invocation inside it:
> `approval bash go *` allows `go mod tidy` but *not* `cd /work && go mod tidy`,
> since a rule matching anywhere in the line would be trivially escapable.

Anchoring is the correct design. The part that bites is unstated: that `*` will not
cross a newline. Nobody writing `approval bash *` believes they are writing a
single-line filter, and the phrase "the whole command string" actually reads as
*reassurance* that the whole thing is considered.

## Why it cost an arm rather than three runs

It does not fail uniformly — it partitions runs by **whether the model's chosen
method fits on one line**. Single-line approaches score; multi-line approaches
abort. That is a selection effect on *method*, correlated with how sophisticated
the approach was, so the runs that survived were no longer a random sample of
anything. I voided all 12.

For an eval harness this is the worst available failure mode: the policy produces a
number that is **scoreable and about the policy**. Nothing in a summary reveals it;
it took reading a transcript.

## Suggestions, in the order I'd want them

1. **Make `*` match newlines in approval patterns.** The whole-string anchoring that
   makes the rule safe is untouched; only the wildcard's reach changes.
2. Failing that, **one sentence in the `bash` row**: *"`*` does not match a newline,
   so `approval bash *` will not approve a multi-line command such as a heredoc."*
3. **Have the denial name the near-miss.** `approval_reason: "default-deny (pattern
   'bash *' did not match: command spans 33 lines)"` would have turned a voided arm
   into a one-line fix. This is the cheap one and it generalises past this bug.

Happy to test a patch — the corpora are pinned and the arm is reproducible.


## Fix

**Suggestion 1, taken as written.** `ApprovalPatterns.Add` compiles its glob with
`RegexOptions.Singleline`, so `.` crosses a newline. Anchoring is untouched — a pattern
still has to match from the first character — so this widens the wildcard's reach, not
the rule's escape surface.

The widening is real and worth stating plainly: `approval bash git *` now also matches

```
git status
rm -rf /tmp/x
```

That is not a new class of hole. The same pattern already matched
`git status && rm -rf /tmp/x` before this change, because a trailing `*` has always
spanned command separators; a newline is simply another separator, and the
whole-string anchoring that the docs justify is what stops a rule being escaped by
burying the program on a later line. `IsApproved_MultilineCommand_StillAnchoredAtTheStart`
pins that, and passed before and after.

Suggestion 2 (a sentence in the `bash` row) landed too, inverted — the row now says `*`
*does* cross a newline, since that is the behaviour a reader needs to know.
Suggestion 3 (name the near miss) is
[`Denials_Do_Not_Name_The_Near_Miss.md`](Denials_Do_Not_Name_The_Near_Miss.md), fixed
alongside this.

One consequence worth noting for the sibling report: `BashRemedy` synthesises
`approval bash python3 *` for a heredoc, and that suggestion **now works**. The parent
report flagged it as "confidently actionable and the action does not work, which is
strictly worse than saying nothing" — that is resolved by this fix rather than by
changing the remedy.

**Tests.** Four in `nb.Tests/ApprovalPatternsTests.cs`; the three asserting new behaviour
were confirmed failing beforehand, and the anchoring control passed both before and after.
Verified end to end: the heredoc program that voided the arm now records
`"approved":"allow"`.
