# A `no-match` denial says "nothing in the approval policy allows it", but the gate is `Trust` in the config

Status: Open (2026-09-04) — found while building a documentation-retrieval eval
harness, same work as [`Approval_Bash_Glob_Does_Not_Match_Newlines.md`](Approval_Bash_Glob_Does_Not_Match_Newlines.md).
Provider `LocalCoder`/`qwen3-coder-next`.

**This voided a second arm.** Diagnosability, not correctness — the behaviour matches
the documented ladder exactly.

## Symptom

Program:

```
approval default prompt
approval sandbox bwrap
```

Config (inherited, from another experiment): `"Trust": false`.

6 denials across 4 of 10 runs, every one `approval_reason: "no-match"`, every one a
read-only command:

```
find ./docs -type f -name "*.md" | head -20
grep -n "<identifier>" …/docs/data/reference-table.json | head -10
```

The error handed back to the model:

```
Error: bash (Run): <command> was denied — nothing in the approval policy allows it.
This will not succeed on retry; nothing in this run …
```

Evidence: `build/eval/real-a4/q11__r2/transcript.jsonl` and five siblings
under `build/eval/real-a4/`.

## Why the output is not wrong, exactly

It is the documented ladder, working:

> `prompt` (the default) tries explicit patterns, then the built-in safe-command
> list, then trust + sandbox

`Trust: false` removes the third rung, the safe list does not cover pipelines, and
there were no explicit patterns. Denial is correct.

**But *"nothing in the approval policy allows it"* sends the reader to the
program's `approval` directives** — which are right there, and look permissive:
default `prompt`, sandbox `bwrap`. Neither the message, nor `approval_reason`, nor
anything else on the event mentions trust, or that the gate is a **config file in a
different repo**. I re-read the program a dozen times before finding the `default`
row in the docs and realising the third rung was conditional.

The message is also confident in a way that compounds it: *"This will not succeed on
retry"* is true and correctly discourages the model from looping, but it reads to a
human debugging the harness as *"this command is not allowed here"* rather than
*"one of three rungs is switched off elsewhere."*

## Why it cost an arm

One of the six denials was
`grep -n … docs/data/reference-table.json | head -10`. The agent was deliberately
reaching for a dataset — and **whether agents reach for the dataset was the
treatment variable the arm existed to measure.** My `used_dataset` metric would have
undercounted by construction, and the questions most likely to provoke a pipeline
had not run yet. I stopped the arm at 10 of 39 runs.

Setting `"Trust": true` (nothing else changed) took the per-run denial rate from
40% to 18%. Verified on the worst-affected question: 2 denials per replicate before,
0 after.

## Suggestion

Name the rung that failed, and name trust when trust is why it failed:

```
approval_reason: "no-match (default=prompt: no explicit pattern; not on the
                  safe-command list; Trust=false so the sandbox rung was skipped)"
```

`no-match (Trust=false)` alone would have been enough. The three-rung design is
good; the failure just needs to say which rung it fell off — and specifically to
distinguish *"your policy does not cover this"* from *"a rung is disabled."*

There is a shared shape with the newline bug: both denials were **silent about the
near-miss**, and in both cases the near-miss was the whole diagnosis. A reason
string that says what almost matched would retire both reports — filed as
[`Denials_Do_Not_Name_The_Near_Miss.md`](Denials_Do_Not_Name_The_Near_Miss.md).

## Smaller note in the same area

**`approval default prompt` is a confusing name when nothing prompts.** The docs are
explicit and pre-emptive about it —

> the names are permissiveness tiers, not dispositions, and neither one asks

— and I still mis-set my expectations from the name alone, then wrote a comment in
my own harness explaining it to the next reader. `approval default lenient` /
`strict`, keeping `prompt` / `deny` as aliases, would remove the trap without
breaking a single existing program.
