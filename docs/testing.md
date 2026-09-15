# Testing affordances

Ways to exercise nb, a program, or a harness without spending tokens, and the two test
suites in the repo.

## Mock provider

Returns `"OK"` by default, or the value of the `Response` config key, or an inline
override: a prompt that starts with `MOCK:response=<text>` gets that text back
verbatim. No API key, no network.

```bash
echo 'run MOCK:response=hi' | ./nb - --output jsonl
```

Other `MOCK:` forms script tool calls, loops, and failures. They are listed in
`Providers/Mock/MockProvider.cs`, and `evals/run.sh` uses most of them. When a program
declares an `oracle`, a `MOCK:oracle=<ids|DONE|MISS>` rider inside the subject's
scripted reply scripts the judge's verdict too, so one line scripts both halves of a
resolution.

## Fake tools

nb reads `fake-tools.yaml` and treats those definitions as normal tools; when the model
calls one, nb returns the configured response. See `fake-tools.example.yaml` for the
format. Fake definitions override MCP definitions, so you can fake a destructive action
or retune a tool description for alignment testing without touching the real server.

Responses support macros, so each invocation produces fresh data:

| Macro | Description | Example |
|-------|-------------|---------|
| `{{$guid}}` | Random UUID | `a3b1c2d4-...` |
| `{{$timestamp}}` | Current UTC time (ISO 8601) | `2026-02-25T14:30:00Z` |
| `{{$int}}` | Random integer | `483291` |
| `{{$int(1,100)}}` | Random integer in range | `42` |
| `{{$counter.name}}` | Auto-incrementing counter | `1`, `2`, `3`... |
| `{{$param.fieldname}}` | Echo back a tool argument | value of `fieldname` |
| `{{$choice(a,b,c)}}` | Random pick from list | `b` |
| `{{$random_string}}` | Random alphanumeric (8 chars) | `xK9mPq2r` |
| `{{$random_string(16)}}` | Random alphanumeric (custom length) | `xK9mPq2rT5nLw8yZ` |

```yaml
response: '{"id": "{{$guid}}", "status": "{{$choice(pending,active,completed)}}", "created_at": "{{$timestamp}}"}'
```

## The eval suite

`evals/run.sh` drives the built binary end to end against the Mock provider and asserts
on model-visible strings: refusal text, exit reasons, trailer fields. CI runs it after
`dotnet test`, and a change that passes the unit suite can still fail here, so run both:

```bash
dotnet build && dotnet test --no-build && ./evals/run.sh --skip-llm
```

Without `--skip-llm` the script also runs one LLM-as-judge eval against the configured
real provider.

## The oracle bench

`evals/oracle-bench/` measures the oracle's judge prompt against a real model, N samples
per case, scored on what nb did. The Mock provider replays a fixed assistant message and
`oracle provider <entry>` sends the side call to the model under measurement. It is not
part of CI. Run it before and after changing the judge prompt; the README in that
directory explains the case format and how to read the summary.

```bash
./evals/oracle-bench/run.sh --provider LocalLlm --n 5
```

## Validating a corpus

`nb --validate <program>` parses and checks a program without running it and exits 1 on
any error. It is cheap enough to run over a whole corpus in CI before any tokens are
spent.
