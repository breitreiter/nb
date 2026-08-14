# Tool names diverge from the model's native surface, and it costs tokens

Status: Open (2026-08-11) — measured on a local harness driving nb against a
containerized Go fixture, with `qwen3-coder-next` served by llama.cpp.

## Symptom

A run that should have been mostly cheap edits was 98% input tokens: **411,938
in against 8,892 out** over 31 turns, and it aborted on `token_budget` before
finishing the task. The model had rewritten three source files wholesale, several
times, rather than editing them.

Tool usage across that run: **`write_file` ×4–5, `edit_file` ×1.**

## Likely cause

nb's file tools are semantically identical to the ones Qwen Code exposes, but
named differently — and `write_file`, the expensive one, is the one whose name
matches.

| Qwen Code (native) | nb |
| --- | --- |
| `edit(file_path, old_string, new_string, replace_all?)` | `edit_file(path, old_string, new_string, replace_all)` |
| `write_file(file_path, content)` | `write_file(path, content)` |

So the argument shapes line up exactly; what differs is the tool name (`edit` vs
`edit_file`) and the first parameter (`file_path` vs `path`). A model
post-trained on the Qwen Code surface recognises `write_file` immediately and has
to generalise to reach `edit_file` — which is precisely the asymmetry the usage
counts show.

The second half of the gap is instructional. Qwen Code's system prompt tells the
model to use `edit` for modifying existing files, `write_file` for creating them,
and to *"prefer editing existing files over creating new ones"*. nb deliberately
injects no persona (§5.5: *"nb injects no persona; a program gets only the
`system` directives it writes"*), so a program that does not say this leaves the
model with no steer at all.

## Why it matters beyond one run

- **Cost.** Whole-file rewrites are quadratic-ish in a conversation: the file
  goes out in the response *and* comes back in every subsequent turn's context.
  This single run spent 400k+ tokens on a task whose finished diff is ~250 lines.
- **Comparability.** For anyone benchmarking models through nb, this confounds
  the measurement — a model penalised for "rewriting whole files" may simply be
  failing to recognise the tool. That is a property of the harness, not the
  model.
- **Truncation risk.** Whole-file rewrites of a large file are also where a
  model runs out of output budget mid-file.

## Suggested fix

A per-provider (or per-model) **tool naming profile**, so the advertised surface
matches what the active model was trained against:

```
tools profile qwen      # edit, write_file, file_path…
tools profile default   # edit_file, write_file, path…
```

Aliases would be enough — the implementations do not need to change, only the
advertised `name` and the parameter key. If a full profile system is too much,
the cheap 80% is:

1. Accept `file_path` as an alias for `path` on both file tools.
2. Advertise `edit` as an alias of `edit_file`.

Worth considering alongside it: an opt-in one-line tool-usage preamble
(`system` text nb can supply on request), so callers do not each have to
rediscover that the model needs telling to prefer edits. That stays consistent
with "no persona by default" while making the good path reachable.

## Partial confirmation, 2026-08-12 — and a correction

The aliasing change has not been tried. What *has* been tried is the cheap
half: telling the model the surface from a `system` directive — *"to modify an
existing file use `edit_file(path, old_string, new_string)`; use `write_file`
only to create a new file; prefer editing over rewriting"*. Same model, same
task, same fixture, one added directive.

| | before | after |
| --- | --- | --- |
| `edit_file` calls | 1 | **10** |
| `write_file` calls | 6 | 3 |
| exit reason | `token_budget` | `ok` |
| input tokens | 411,938 | 396,315 |

**The naming/instruction gap is real** — a 10× swing in tool selection from one
sentence, and a task that previously aborted now completes. That is the part of
this report to act on.

**But the token claim above was wrong, and I'd rather correct it than leave it
standing.** I argued whole-file rewrites were the cost driver; input tokens moved
less than 4%. The dominant term is *turns × accumulated context* — every turn
resends the conversation whatever shape the writes take. What actually improved
was work per token: the same spend produced six times as much finished work.

So the case for per-provider toolsets rests on **correctness and completion**,
not on token savings: the model picks the right tool, finishes the job, and
stops truncating its own output. Anyone weighing this against the cost of a
breaking change to the published provider interface should weigh it on those
grounds.

Also worth noting: if a one-line `system` steer recovers most of the benefit,
that may be the right shipping order — document the steer now, land toolsets when
the interface change is affordable.

## Confirmation with a real costume, 2026-08-14

The aliasing fix has now been built and measured. `harness qwen-code`
(`nb.Core/Harness/QwenCodeHarness.cs`) advertises qwen-code's actual names and
parameter spellings — `edit`, `glob`, `grep_search`, `list_directory`,
`run_shell_command`, `file_path` throughout — over nb's unchanged tools.

**Method.** Three Python files (~30 lines total), each using `print()` for logging.
Task: replace every `print(` with `log(` and add an import. Same model
(`qwen3-coder-next` on the local box), same fixture copied fresh per run, same
minimal `system` directive in both arms — deliberately *without* the tool-preference
sentence from the section above, so the advertised surface is the only variable.
Three replicates per arm.

| | baseline (nb surface) | costume (`harness qwen-code`) |
| --- | --- | --- |
| run 1 | `edit_file` ×6, `write_file` ×0 | `edit` ×3, `write_file` ×0 |
| run 2 | `edit_file` ×0, **`write_file` ×3** | `edit` ×3, `write_file` ×0 |
| run 3 | `edit_file` ×0, **`write_file` ×3** | `edit` ×11, `write_file` ×0 |
| mean edits / writes | 2.0 / 2.0 | 5.7 / 0.0 |
| mean turns | 9.7 | 12.0 |
| mean input tokens | 39,769 | 54,375 |
| task correct | 3/3 | 3/3 |

**Tool selection moves, consistently.** Under nb's surface, two of three runs
abandoned editing entirely and rewrote all three files wholesale. Under the costume,
every run edited and `write_file` was never called once. This is not an artefact of
removing the option: `write_file` keeps its name in qwen-code and is advertised in
both arms, so the model had the same choice and made a different one. That is the
original report's hypothesis reproducing.

**Tokens and turns do not support the costume**, which is consistent with the
correction above and worth restating: the costume averaged *more* input and more
turns. One outlier drives much of it (costume run 3 made eleven small edits), and the
cheap baseline runs are cheap precisely *because* whole-file rewrite is nearly free on
a 12-line file. The case rests on tool selection, not on cost.

**This fixture cannot test the completion claim.** 3/3 correct in both arms — at this
size a whole-file rewrite is a perfectly good strategy. Rewriting only becomes
pathological on files large enough to truncate mid-write, which is where the original
411,938-token abort came from. Testing that needs a fixture with files of a few hundred
lines.

**Caveats.** n=3 per arm against a non-deterministic model; a 2/3-vs-0/3 split is
directionally consistent with the 6:1 write:edit ratio in the original report but is not
on its own strong. The costume is tool-surface-only — qwen-code's system prompt is not
vendored yet — so this measures the surface half alone, where the 2026-08-12 entry
measured the prompt half alone.

## Where the fix lives, 2026-08-14

The "tool naming profile" suggested above is now `plans/harness-emulation.md` —
harnesses as C# classes deriving from `NbHarness`, selected per program. The
qwen-code costume there subsumes this report's suggested fix, and the fixture and
numbers above are the measurement that validates it.
