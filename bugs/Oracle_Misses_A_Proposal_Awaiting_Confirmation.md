---
kind: bug
title: The oracle misses when the assistant proposes a value and waits for confirmation
created: 2026-09-14
updated: 2026-09-14
status: current
state: open
severity: high
cluster: oracle-resolver
---

# The oracle misses when the assistant proposes a value and waits for confirmation

The oracle resolves an **open question** reliably and a **proposal awaiting
confirmation** almost never — same content, same sheet, same model, same length.
Measured on a local coder model (`qwen3-coder-next`, OpenAI-compatible server),
five samples per cell:

| the assistant's last message | verdict |
|---|---|
| "What should I call this service, and which environment is it? Please confirm before I write any configuration." | the right id, **5/5** |
| "I'll use `<name>` as the service name and `<env>` as the environment. Please confirm before I write any configuration." | MISS 4, DONE 1, **0 hits** |

The sheet is identical in both cells and contains an entry whose id is almost
the message's own words. The second phrasing is the one an assistant produces
when its instructions tell it to propose values and wait — which is a common
shape, and in the case that found this, the shape the task under test *required*
on every run. That made the behaviour being studied unmeasurable.

## Why it matters more than a miss usually would

`MISS` at least ends the run loudly. **`DONE` ends it as `ok`** with the
unanswered question sitting in the transcript as the last `assistant_text`, and
everything downstream reports on a run that stopped mid-conversation as though it
had finished. In the sampling above the stock prompt produced `DONE` between one
and four times in five on a message that was plainly waiting on the user.

`ParseVerdict` also treats an **unreadable** verdict as `DONE` — conservative for
a malformed reply, but it means every way the judge can ramble lands on the quiet
branch rather than the loud one. Consider making an unparseable verdict `MISS`.

## Repro

1. An answer sheet with an entry covering some value the user knows:

   ```markdown
   ## service-name-and-environment

   Call it `orders-api`, and the environment is `production`. Those are the names
   it goes by everywhere else we run it, so please use exactly those.
   ```

2. Run any program with `oracle @<that sheet>` where the assistant's final
   message proposes values rather than asking for them:

   > I'll use `billing-api` as the service name (derived from the module name) and
   > `development` as the environment. Please confirm before I write any
   > configuration.

3. Expected: `service-name-and-environment`, so the sheet's real values go back as
   a user turn and the run continues. Actual: `MISS`, or `DONE` — neither
   continues, and `DONE` is scored as a completed run.

`OracleResolver.BuildPrompt` is a static literal, so the prompt can be replayed
outside a run: parse the sheet with `AnswerSheet.Parse`, render it, build the
prompt with the assistant's last message, and call the same provider directly.
That is how the numbers above were taken.

## What was tried, and what it cost to find out

Four prompt variants, five samples each, all against the same sheet and message:

| variant | propose form | ask form (regression) | finished-report guard |
|---|---|---|---|
| stock prompt | 0/5 | 5/5 | — |
| added a paragraph above the reply bullets | 0/5 | 3/5, **2 echoed the bullet list** | DONE 5/5 |
| one inline clause: *"Proposing a value and waiting for the user to confirm it counts as asking."* | 0/5 | 5/5 | — |
| reworded the first bullet to *"entries that cover what the assistant needs from the user … including when it has proposed a value … and even when the entry's value differs from the one proposed"* | 0/5 | 5/5 | — |
| **two worked examples** (one propose → id, one finished → `DONE`) | **2/5** | **5/5, none malformed** | **DONE 5/5** |

Three separate *instructions* moved it not at all, including one that named the
exact objection the model appears to be making. **Examples were the only thing
that moved it**, from 0/5 to 2/5, and they also cleaned up the output format that
the added paragraph had degraded. 2/5 is not reliable enough to depend on, but it
is the direction that works.

The model does not seem to be failing to follow the instruction. It looks like a
semantic judgement: asked whether an entry saying *"call it `orders-api`"*
"answers" a request to confirm `billing-api`, it decides no. That reading is
defensible and useless — the difference between the two values is exactly what the
user needs to tell the assistant, and correcting a wrong proposal is the most
valuable thing the sheet can do.

The wording of the prompt may be feeding that. It asks whether the message is
*"waiting on the user for **information**"* and which entries *"**answer** what the
assistant is asking"*. A confirmation request is not obviously a request for
information — the assistant already has values — and a conservative judge told to
default to `DONE` when in doubt is pushed toward the quiet branch.

## Suggested directions

- **Ship worked examples in the prompt.** The only lever that moved the number,
  and it fixed an output-format problem at the same time. Worth tuning further —
  more examples, or one drawn from a corrected proposal specifically.
- **Consider making the judging guidance configurable**, while keeping the output
  contract (`ids | DONE | MISS`) fixed in nb, since `ParseVerdict` depends on it.
  A program author knows what their agent's messages look like. The usual
  objection — that a tunable judge is a judge you tune until it answers — is
  weaker here than it looks, because the oracle *selects and never authors*: a bad
  override can pick the wrong entry but cannot invent an answer that is not on the
  sheet. Any override should land in the run's recorded condition so two runs with
  different judges are not silently compared.
- **Record the verdict.** `oracle_miss` currently emits one warning and keeps no
  evidence: the raw verdict text is not in the transcript or the trailer, so there
  is no way to tell "the sheet genuinely lacks this topic" from "the judge could
  not parse the shape of the ask". The first is a finding worth reading; the second
  is this bug. Everything above had to be reconstructed by rebuilding the prompt
  and replaying it.
