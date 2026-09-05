---
kind: bug
title: 'With `Trust: true` and `sandbox bwrap`, a bare `find … 2>/dev/null` is still denied'
created: 2026-09-04
updated: 2026-09-04
status: current
state: open
severity: low
cluster: approval-diagnosability
---

# With `Trust: true` and `sandbox bwrap`, a bare `find … 2>/dev/null` is still denied

Status: Open (2026-09-04) — **isolated 2026-09-04**; the repro script was never needed.
Found while building a documentation-retrieval eval harness; provider
`LocalCoder`/`qwen3-coder-next`. Same work as
[`No_Match_Denial_Does_Not_Name_The_Trust_Rung.md`](No_Match_Denial_Does_Not_Name_The_Trust_Rung.md).

Cost me turns, not an arm. Filed because the docs promise something broader than
what I observed, and I would rather you knew than that I guessed.

## Symptom

After setting `"Trust": true` — program unchanged, still
`approval default prompt` + `approval sandbox bwrap` — the per-run denial rate fell
but did not reach zero:

| arm | runs | runs with ≥1 denial | denials | `Trust` |
|---|---|---|---|---|
| `fs-a2` | 12 | 2 (17%) | 2 | false |
| `fs-a2b` | 12 | 2 (17%) | 2 | false |
| `fs-a3` | 12 | 1 (8%) | 1 | false |
| `real-a4` | 10 | 4 (**40%**) | 6 | false |
| `real-a5` | 22 | 4 (18%) | 5 | **true** |

The five remaining denials, all `approval_reason: "no-match"`:

```
cd <ws> && grep -r "<term>" docs/reference/*.md 2>/dev/null | head -20
cd <ws> && grep -h "<term>" docs/reference/*.md 2>/dev/null | grep -E "…" | head -20
cd <ws> && cat docs/index.md | grep -E "…"
find <ws> -name "glossary.md" 2>/dev/null
find <ws>/docs -type f -name "*.md" -o -name "*.css" …
```

Three are compound, and the whole-string rationale in the docs covers those
squarely. **The last two are not.** `find <path> -name "glossary.md"
2>/dev/null` is a bare `find` with a stderr redirect — no pipe, no `&&`, no
substitution — running inside a bwrap sandbox that is read-only with no network.

Against the documented behaviour of the rung:

> `--trust`, which auto-approves any **non-dangerous** command in the sandbox

A `find` in a read-only sandbox is about as non-dangerous as a command gets.

Evidence: `build/eval/real-a5/q11__r3/transcript.jsonl` and
`build/eval/real-a5/q14__r3/transcript.jsonl`, in a private harness repo outside
this tree.

## What I have not established

Whether the trust rung is **not being reached**, or whether `non-dangerous` excludes
anything containing a shell metacharacter. Those want different fixes and I am not
guessing between them in a bug report.

## Repro, written and not yet run

`repro-trust-no-match/probe.sh` walks one variable at a time as separate
single-instruction runs, printing `approved` / `approval_reason` per case: bare `ls`,
bare `find`, `find … 2>/dev/null`, `find … -o …`, bare `grep`, `grep … | head`,
`grep … 2>/dev/null`, `cd … && grep`, absolute-path `grep`, `cat`, `wc`.

```bash
NB=/path/to/nb NB_CONFIG=/path/to/config.json ./repro-trust-no-match/probe.sh
TRUST=false NB=… NB_CONFIG=… ./repro-trust-no-match/probe.sh   # same matrix, trust off
```

It derives its own config copy with `Trust` flipped (0600, since configs carry API
keys) so it does not disturb the caller's. I will append the matrix here once my
current arm stops using the model.

If the answer turns out to be "a metacharacter disqualifies a command from the trust
rung", that is probably fine as *behaviour* and wants one sentence in the `sandbox`
row — the fix would be documentation, not code.

## One distinction that may be worth encoding

**This was not a confound; the two in the sibling reports were.** The affected runs
reached the corpus repeatedly through the `grep` and `read_file` *tools* on either
side of their denials, so the policy blocked one **spelling** of something the agent
could and did accomplish another way. In `real-a4` the denied command **was** the
treatment.

Same mechanism, and only one of the two invalidates an experiment. Anything that
lets a harness tell them apart cheaply — see
[`Denials_Do_Not_Name_The_Near_Miss.md`](Denials_Do_Not_Name_The_Near_Miss.md) — — a reason string precise enough to grep, or
a trailer field counting denials that had no successful alternative — is worth more
to me than the denials going away.

*(Related and verified fine: the `result` trailer's `denied` count matches the
`tool_call` events with `approved: "deny"` in all 76 transcripts I have, including
the 14 runs with at least one denial. No mismatches. Worth not chasing.)*


## Diagnosis, 2026-09-04 — the redirect target is what gets sandbox-checked

The near-miss channel from
[`Denials_Do_Not_Name_The_Near_Miss.md`](Denials_Do_Not_Name_The_Near_Miss.md) answered
this on the first run, with no repro script and no bisect. That report predicted it would
turn this into "a five-minute question instead of a repro script"; recording that it did.

```console
$ nb --config trust.json f.nb     # Trust: true, program unchanged
[nb] denied: bash (Write): /dev/null — nothing in the approval policy allows it.
       near miss: default=prompt; no approval bash pattern matched (none configured);
                  not on the safe-command list; trust rung refused: path '/dev/null'
                  is outside the trust sandbox (cwd '…' + system temp)
```

**Cause.** `find /etc -name hosts 2>/dev/null` is classified as **`Write` → `/dev/null`**.
`CommandClassifier` takes the *redirect target* as the command's path, so the trust rung
sandbox-checks `/dev/null` rather than anything the command reads, finds it outside cwd +
system temp, and refuses. The `find` is incidental: any command with a `2>/dev/null` on it
gets classified as a write to `/dev/null` and denied under trust.

That also explains the shape of the original measurement — the denial rate fell under
`Trust: true` but did not reach zero, and the survivors were the commands that happened
not to carry a redirect.

**Two candidate fixes, and they are not the same decision.**

1. **Treat `/dev/null` as a trusted write target.** Narrow, obvious, and fixes the
   observed case: writing to the null sink is not an escape from any sandbox. It leaves
   the general problem — a redirect to a real path outside cwd still classifies the whole
   command as a write to that path, which is arguably *correct*.
2. **Stop letting a redirect target stand in for the command's path.** Broader, and it
   touches `CommandClassifier`, which the approval display also reads (the console line
   said `bash (Write): /dev/null` for what is plainly a read). This is the one that makes
   the classification honest, and the one with the wider blast radius.

Both are behaviour changes to the trust rung, so they want the decision made deliberately
rather than folded into a diagnosability fix. Left open on that basis, not on lack of
information.
