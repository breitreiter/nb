# `approval bash *` allows only single-line commands, so a heredoc is denied

Status: Open (2026-09-04) — found while building a documentation-retrieval eval
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
