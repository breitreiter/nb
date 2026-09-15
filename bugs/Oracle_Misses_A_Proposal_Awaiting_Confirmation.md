---
kind: bug
title: The oracle misses when the assistant proposes a value and waits for confirmation
created: 2026-09-14
updated: 2026-09-14
status: current
state: fixed
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


## Fix (2026-09-14)

Two things landed together: a bench that reproduces the failure on demand, and a
rewritten judge prompt chosen by it.

**The bench.** `evals/oracle-bench/` runs every case through the real product path —
the Mock provider replays a fixed assistant message as the subject's turn and a real
model, named by the new `oracle provider <entry>` directive, judges it — N samples per
case, scored on what nb actually did (`keys`, `oracle_miss`, or `ok`). Sixteen cases:
the two cells above, four more proposal shapes (long, markdown-heavy, an assumption
with an opt-out, a proposal that already matches the sheet), a bare "shall I write
it?", two-question asks, off-sheet asks, and three finished-report controls. Its
`done-on-waiting` count isolates the failure this report is about.

**What the bench showed that hand replay had not.** On the same model and the same
sheet, the stock prompt was far worse than the table above: **26/80** overall, and the
"ask form" control that had measured 5/5 came in at **1/5**. One reason is a defect the
report did not see: the prompt asks for "the ids of the sheet entries" without saying
what an id is, and the model answered with **ordinals** — `1`, `1,2` — which
`ParseVerdict` cannot read and so quietly scored as `DONE`. The other is that the side
call carried no temperature, so it ran at the server's default and the earlier 5/5 was a
sampling artefact. The judge is now greedy (`Temperature = 0`).

**What moved it.** Four variants on `qwen3-coder-next`, five samples per case:

| variant | pass | done-on-waiting |
|---|---|---|
| stock | 26/80 | 15 |
| + ids named explicitly, greedy | 36/80 | 10 |
| + the judge **is the user** (see below) | 74/80 | 5 |
| + two worked examples | 75/80 | 0 |
| + "only the entries that speak to what it is waiting on" | **80/80** | **0** |

Naming the ids fixed the ordinal problem and nothing else: every proposal cell stayed
0/5. The reframe is what fixed the proposals. The stock prompt asked a third party
whether the message was "waiting on the user for **information**" and which entries
"**answer**" it — and a request to confirm `billing-api` is not a request for
information, and an entry saying `orders-api` does not "answer" it. The new prompt puts
the judge *in the user's seat*: the sheet is what you know and would say; the assistant
has handed you the turn; which entries are your reply? Waiting-on-you is defined to
include a proposal that stops for a say-so, and a differing entry is named as the reply
that corrects it. Under that frame the correction is the obvious move rather than a
semantic stretch. The examples closed the last `DONE` on a clear off-sheet ask; the
selection guard removed a harmless over-selection (adding `customer-name` to a
deploy-only ask).

**On a frontier reasoning judge** (GLM 5.2 hosted on Cloudflare Workers AI, via
`oracle provider`, three samples per case): the shipped prompt scored **48/48**,
done-on-waiting 0, ~15 s and ~115 output tokens per verdict. The stock prompt on the same
model over the six proposal cases scored 16/18 — one `DONE` on a proposal that matched
the sheet, one `MISS` on the corrected-proposal-with-proceed case — so the bug was mostly
a small-model problem, but not only one; a strong judge still dropped one in nine
proposals under the old frame. One difference in kind: the strong judge read the thin
"Deploy to staging." entry as a hit where the local models judged `MISS`; both are
accepted by that case.

Local `glmchat` was only spot-checked: the box was at its memory ceiling and a verdict
there costs 25–110 s of reasoning. Five samples agreed with expectations; one
`finished-report` sample judged `MISS`, on a box under pressure. Not swept further.

**Also in this change.** `oracle provider <entry>` is a real directive, not a bench
hook — it is the `oracle model` deferral from the plan, and a cheap judge beside an
expensive subject is its production use. An unbuildable entry aborts at the first
judgement, as `provider` does.

**Correction, same day:** "the judge is now greedy" lasted one run against Claude. The
Claude 5 family rejects `temperature` outright (*"temperature is deprecated for this
model"*), so a forced 0 broke every judgement on those entries. The side call now uses the
judge entry's configured `Temperature`, as the main run does; the bench sets 0 on its
cloned entry by default (`--temperature none` to unset), so the numbers above still hold.

**Also landed:** the raw verdict is recorded — `verdict` on the oracle-supplied `user`
turn, `oracle_verdict` on the `result` trailer (the judgement that ended the run).

**Not done.** Unparseable verdicts still land on `DONE`; that stands as a suggestion.

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
