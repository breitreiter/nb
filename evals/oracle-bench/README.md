# Oracle bench

Measures the oracle's verdicts (`plans/oracle-resolver.md`) against a real judge model,
case by case, N samples each. It exists because the judge failed systematically on one
message shape — a proposal awaiting confirmation — and the failure was only visible by
rebuilding the prompt by hand (`bugs/Oracle_Misses_A_Proposal_Awaiting_Confirmation.md`).

Every cell runs the real product path: the Mock provider replays a fixed assistant
message as the subject's turn, and `oracle provider <entry>` sends the side call to the
model under measurement. What is scored is what nb actually did — the `keys` it
selected, `oracle_miss`, or a run that ended `ok` — not a replay of the prompt.

```bash
dotnet build
./evals/oracle-bench/run.sh --provider LocalLlm --n 5            # every case, the configured entry
./evals/oracle-bench/run.sh --provider LocalLlm --case 'propose-*' --variant my-prompt
./evals/oracle-bench/run.sh --provider LocalLlm --endpoint http://host:port/v1 --model other
```

The judge is any `ChatProviders` entry in `bin/Debug/net10.0/appsettings.json` (or
`--config`); `--endpoint` / `--model` override that entry for the run. Results go to
`out/<timestamp>-<model>-<variant>/`: `results.jsonl` (one line per sample, with the
verdict, pass/fail, exit reason, the judge's raw verdict text, the oracle's warnings,
timing and output tokens),
`summary.md`, and every run's program, transcript and stderr under `runs/`.

## Reading the summary

Two numbers matter. **Overall pass** is the headline. **done-on-waiting** is the
dangerous one: a `DONE` on a message that was waiting on the user ends the run as `ok`,
so everything downstream scores a run that stopped mid-conversation as finished. A
`MISS` at least ends loudly.

Per-sample marks in the progress line: `H`/`M`/`D`/`E` for hit, miss, done, error;
lower-case when the sample failed its expectation.

## Adding a case

`cases/<name>.md`:

```
sheet: service.md
expect: hit service-name-and-environment
---
I'll use `billing-api` as the service name and `development` as the environment.
Please confirm before I write any configuration.
```

`expect` is `hit id[,id]`, `miss` or `done`; `|` separates alternatives that all count
as a pass (use sparingly — a case that accepts two verdicts only measures `done`).
Hits compare as id sets. Sheets live in `sheets/` and are shared across cases.

Write the message as an agent would actually write it — length, markdown, the summary
of work before the ask — not a minimal sentence. The minimal forms are already here;
the realistic ones are where the judge fails.

## Comparing prompt variants

The prompt is code (`OracleResolver.BuildPrompt`). Edit, `dotnet build`, run with
`--variant <label>`; the label rides on every result line so runs can be joined.
Don't rebuild while a bench run is in flight — it drives the built binary.
