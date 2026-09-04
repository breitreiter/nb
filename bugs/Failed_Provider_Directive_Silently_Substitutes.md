---
kind: bug
title: 'A `provider` directive that can''t build a client silently answers from a different provider'
created: 2026-08-13
updated: 2026-09-04
status: current
state: fixed
severity: high
cluster: provider-truthfulness
---

# A `provider` directive that can't build a client silently answers from a different provider

Status: **Fixed 2026-09-04** — the first two of the three suggested fixes, taken as
written. Originally: Confirmed (2026-08-13) against `bin/Debug/net10.0/nb` at master 418941a.
Found while testing rate-limit retry — a `provider Mock` test run quietly went to
a live model instead.

## What happens

When a `provider` directive's client can't be built, `SwapClient` records a
warning and returns, leaving the **previous** client in place. The run then
answers from a provider the program did not ask for, exits 0, and emits a
transcript with nothing in it to say so.

```console
$ cat broken.json
{ "ActiveProvider": "Mock",
  "ChatProviders": [
    { "Name": "Mock", "Response": "from-mock" },
    { "Name": "Broken", "Provider": "OpenAI", "Model": "gpt-5" } ] }

$ cat b.nb
provider Broken
run hi

$ ./nb b.nb --output jsonl --config broken.json; echo "exit=$?"
{"type":"user","turn":0,"text":"hi"}
{"type":"assistant_text","turn":1,"text":"from-mock"}
{"type":"result","turn":null,"exit_reason":"ok","usage":{…},"turns":1,"tool_calls":0}
exit=0
```

`from-mock` is the Mock provider's canned response. The program asked for
`Broken`; the answer came from `Mock`; stdout says the run was fine.

The diagnostic exists, but only on stderr, as chrome:

```
Entry 'Broken' is missing configuration required by 'OpenAI':
  - ApiKey
program: could not build a client for provider 'Broken' model '(default)'
```

## What is and isn't already documented

Warn-and-continue is **deliberate and documented**
(`docs/conversation-program-cli.md:402`):

> **Unknown provider name** → caught by `--validate` (exit 1); at run time a bad
> `provider`/`model` directive warns and keeps the current client. Prefer `--validate`.

So this report is not asking to reverse that policy on its own terms. Two things
it does not cover:

**1. `--validate` does not catch the case above.** The doc's recommended
mitigation checks directive *names* against `ChatProviders[].Name`. An entry that
is present but unbuildable — missing `ApiKey`, missing implementation DLL, a
provider whose `CreateClient` throws — passes validation and then substitutes at
run time:

```console
$ ./nb --validate b.nb --config broken.json; echo "exit=$?"
valid: 2 directive(s).
exit=0
```

Compare the genuinely-unknown name, which validation does catch:

```console
$ ./nb --validate g.nb --config two.json   # `provider Ghost`
error: unknown provider 'Ghost'. Configured: Mock.
invalid: 1 error(s).
exit=1
```

The gap is exactly the class of failure a harness is most likely to hit in
practice — a key that expired, an env var that didn't get exported into a CI
job, a provider directory that didn't deploy. Those all validate clean.

**2. The transcript carries no trace of the substitution.** This is the sharper
problem, and it is independent of the warn-vs-fail policy. The jsonl contains no
`provider` event, no warning event, and `exit_reason: ok`. A consumer reading the
transcript — the machine-readable contract, per §2 — cannot tell that the run
used a provider other than the one the program specifies, and cannot distinguish
this transcript from one where `Broken` worked. Evaluation harnesses that
attribute results per-provider will silently mis-attribute them.

`_warnings` (`nb.Core/ProgramEvaluator.cs:29`) is a stderr-only channel: every
entry, including this one, is chrome. That is fine for `approval key '…'
unknown — ignored`, where the program's *stated* intent still describes what ran.
It is not fine here, where the stated intent and the actual run diverge on the
single most load-bearing field in the envelope.

## Root cause

`nb.Core/ProgramEvaluator.cs:177-185`:

```csharp
private void SwapClient()
{
    var client = _clientFactory(Provider, Model);
    if (client is null)
    {
        _warnings.Add($"could not build a client for provider '{Provider ?? "(default)"}' model '{Model ?? "(default)"}'");
        return;                       // <- previous client stays live
    }
    _conversation.SwitchProvider(client, Provider ?? _conversation.GetCurrentProvider());
}
```

`ProviderManager.TryCreateChatClient` returns null for all of: unknown entry
name, entry naming an unloaded implementation, `CanCreate` false (missing
required keys), and `CreateClient` throwing. Only the first is reachable by
`--validate`.

## Suggested fix

In rough order of value:

- **Make the substitution visible in the transcript.** Whatever the policy, the
  wire format should not claim a clean run of a program whose provider was
  overridden. Either emit the effective `provider`/`model` as events at each run
  point (which would also make `--resolve`'s envelope observable in a recorded
  transcript), or add a warning/notice event type. The former is more useful and
  matches what §7's directive table already implies is recorded.
- **Consider hard-failing a `provider` directive that can't build a client.**
  This differs from an unknown *key* (`approval key '…' unknown`) in kind: an
  ignored approval key narrows the surface, while a substituted provider answers
  the question with the wrong thing. A program that names a provider is
  asserting a dependency, the same way `mcp +server` does — and that case was
  made to hard-fail for the same reason (see
  `bugs/Missing_Mcp_Manifest_Silently_Ignored.md`). If the current behavior is
  kept, `docs/conversation-program-cli.md:402` should stop recommending
  `--validate` as the mitigation without noting it only catches unknown names.
- **Extend `--validate` to buildability.** Have it run the same
  `CanCreate`/implementation-present checks `TryCreateChatClient` does, so
  "missing ApiKey" is a validation error rather than a run-time surprise. This is
  worth doing even if the directive starts hard-failing, since the point of
  `--validate` is to find these before spending tokens.

## Notes

- The REPL hits this too, and there it is arguably the right behavior: a
  mistyped `provider` line should not tear down the session. Any hard-fail should
  be scoped to the program path, where the run is a single non-interactive
  transaction.
- A bad `model` on a *buildable* provider does **not** reach this path: the
  client rebuilds with the requested model and the bogus name fails at the
  provider, which is the right shape. But `model` is applied through the same
  `SwapClient`, so when the *provider* fails to build, the requested model is
  dropped with it — `provider Broken` + `model gpt-5-mini` answered from Mock
  with model `(none)`, having asked for neither. A benchmark sweeping
  provider/model pairs can silently record the same baseline under several
  labels.


## Fix

Two of the three suggestions above landed; the third is deliberately not taken.

**1. The `provider` directive hard-fails.** `SwapClient` (`nb.Core/ProgramEvaluator.cs`)
throws the new `ProviderUnavailableException` instead of warning and returning, so the
previous client is never left live to answer a question it was not asked. The exception
follows the `SandboxUnavailableException` / `McpServerUnavailableException` precedent
this report cites, including its REPL carve-out: both catch filters in `Program.cs` learn
the new type, and because the REPL's filter prints and continues the loop while the
program path's exits 1, the scoping the Notes section asked for falls out of the existing
structure with no mode flag on the evaluator.

```console
$ ./nb --config broken.json b.nb --output jsonl
Entry 'Broken' is missing configuration required by 'OpenAI':
  - ApiKey
Error: could not build a client for provider 'Broken' model '(default)'. The run is
aborted rather than answered by the previously selected provider.
exit=1
```

**2. The transcript names the provider that answered.** `ResultEvent` gains `provider`,
written and read by `TranscriptSerializer`, surfaced as `RunResult.Provider` for library
hosts. Unlike `harness` it is **always emitted**, because omitting it when it matches the
configured default would reintroduce exactly the ambiguity it exists to remove — the
default is config-dependent, so a reader of the transcript cannot resolve it.

```json
{"type":"result","turn":null,"exit_reason":"ok","usage":{…},"turns":1,"tool_calls":0,"provider":"Mock"}
```

**3. `--validate` was left alone.** Extending it to buildability is still worth doing and
is still argued for above; it is a separate change with its own cost (validation would
have to construct clients, which is a side effect `--validate` does not currently have).
The gap is now *documented* rather than implied — §11 says plainly that `--validate`
catches unknown names only, and an eval pins the fact that an unbuildable-but-configured
entry validates clean, so the gap cannot be quietly re-described.

**Scope note — effective *model* is not recorded.** The trailer names the provider, not
the model. Nothing in nb tracks a resolved model today (`ConversationManager` knows
`_currentProviderName` and no counterpart), so recording one means new plumbing through
the client factory. With the hard-fail in place the Notes section's model-drop case can
no longer happen silently — `provider Broken` + `model gpt-5-mini` now aborts rather than
answering as Mock/`(none)` — so what remains is an attribution nicety, not a correctness
hole. Worth its own item if a benchmark needs per-model attribution from the trailer
alone.

**Docs.** `docs/conversation-program-cli.md` §11 replaces the "warns and keeps the current
client" bullet with the abort, and its trailer section documents `provider`.
`docs/conversation-program-api.md` documents the new exception (and how it differs from
`NbStartupException`: same failure, but mid-program rather than at assembly) and the new
`RunResult.Provider`.

**Tests.** `nb.Tests/ProviderSubstitutionTests.cs`, five tests, written before the fix and
confirmed failing against the unfixed evaluator — the substitution cases failed with
*"No exception was thrown"* and the run returning `from-mock`, which is the reported bug
exactly. The happy-path control passed before and after, so the guard is not firing
indiscriminately. Three evals cover the CLI surface (exit 1 + message, the `--validate`
gap, the trailer field), since none of that is reachable from the unit suite.
