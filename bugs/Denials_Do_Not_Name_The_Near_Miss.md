# A denial names the tier that refused, never the rung that nearly matched

Status: Open (2026-09-04) — cross-cutting. Read against `c7a3c93`.
**Severity: medium** — no wrong answers, but it is the reason two separate
experiment arms were voided rather than debugged, and it generalises past both.

This is the shared cause behind
[`Approval_Bash_Glob_Does_Not_Match_Newlines.md`](Approval_Bash_Glob_Does_Not_Match_Newlines.md),
[`No_Match_Denial_Does_Not_Name_The_Trust_Rung.md`](No_Match_Denial_Does_Not_Name_The_Trust_Rung.md)
and [`Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md`](Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md).
Filed on its own because fixing it retires the first two outright and turns the
third into a five-minute question instead of a repro script.

## The claim

`approval_reason` has exactly five values, and all five describe **which tier
answered**, not **what almost worked**:

| value | what it tells you |
|---|---|
| `pre-approved` | an explicit pattern matched |
| `safe` | the built-in allowlist matched |
| `trust` | trust + sandbox matched |
| `default-deny` | the default is `deny` |
| `no-match` | the default is `prompt` and nothing matched |

The three allow values are useful: they name a specific rung, so the reader knows
what to change. The two deny values are the same sentence twice — *"not allowed"* —
and neither carries a byte about the ladder that was just walked. Every debugging
question a denial raises is therefore unanswered by the denial:

- Did my pattern nearly match? (It did — it failed only on a newline.)
- Which rung was switched off? (`Trust: false`, in a config file, in another repo.)
- Was the trust rung consulted at all, or consulted and refused?

## Mechanism — the reason is discarded at the one point it is known

`ApprovalPolicy.DecideBash` (`nb.Core/Shell/ApprovalPolicy.cs:109`) walks the
ladder and knows precisely where each command fell off. It returns that knowledge
on the way up and throws it away on the way down:

```csharp
if (_bashPatterns.IsApproved(command))       return (Allow, "pre-approved");
if (_default == ApprovalDefault.Deny)        return (Deny, null);      // :115
if (!classified.IsDangerous && IsSafeCommand(command))
                                             return (Allow, "safe");
if (_trust && !classified.IsDangerous && bashPresent && IsBashCommandTrusted(…))
                                             return (Allow, "trust");
return (NonMatch, null);                                              // :123
```

Both denial paths return `null`. So `NbHarness` cannot report what happened and
instead **reconstructs a reason after the fact**, from the one input still in
scope (`nb.Core/Harness/NbHarness.cs:737`):

```csharp
public string DenyRung => _approvalPolicy.Default == ApprovalDefault.Deny
    ? ApprovalLedger.DefaultDeny
    : ApprovalLedger.NoMatch;
```

That is a two-valued function of `Default` alone. It does not consult the command,
the classification, the patterns, or `_trust` — it *cannot*, they were never
passed. The rung is not merely unreported; by this point it is unrecoverable.
`Because()` (`:778`) then expands the same two values into the two model-facing
sentences, and `ApprovalLedger` (`nb.Core/Shell/ApprovalLedger.cs:26`) stores them
for the transcript. Three layers faithfully carrying a value that was zeroed out
one call earlier.

The other decision methods never had a reason channel at all: `DecideMcp` (`:127`),
`DecidePath` (`:132`), `DecideFetch` (`:143`) and `DecideSearch` (`:153`) each
return a bare `ApprovalDecision`.

## The remedy channel is the precedent — and it is wrong in the motivating case

nb already accepts that a denial owes the reader something actionable. `Deny()`
(`NbHarness.cs:766`) takes a `remedy` and prints `authorize with: <directive>`,
and `BashRemedy` (`:804`) synthesises one.

But `BashRemedy` takes the first whitespace-delimited token of the command:

```csharp
var program = command.TrimStart().Split(' ', …).FirstOrDefault();
return $"approval bash {program} *";
```

For the heredoc in the sibling report — `python3 << 'EOF'` followed by 32 lines —
that yields `approval bash python3 *`. **Pasting nb's own suggestion into the
program does not fix the run**, because `*` still will not cross a newline. The
denial is confidently actionable and the action does not work, which is strictly
worse than saying nothing.

That is the argument for computing the remedy from the near-miss rather than from
the command's first token: the near-miss already knows *why* the match failed, so
it can either propose a directive that works or say plainly that no directive does
(as `OutsideCwdRemedy` at `:815` already does honestly for the path sandbox).

## What "near miss" means concretely

The three filed instances, and the string that would have ended each in one read:

```
no-match (bash pattern '*' failed: command spans 33 lines and '*' does not
          match a newline)

no-match (default=prompt: no explicit pattern; not on the safe-command list;
          Trust=false, so the sandbox rung was skipped)

no-match (default=prompt: no explicit pattern; not on the safe-command list;
          trust rung reached and refused: command contains a shell redirect)
```

The third is the interesting one — it is the answer the repro script in
`repro-trust-no-match/` was written to obtain, and the policy already knows it at
the moment it decides.

## Suggested shape

Give the deny paths the same reason channel the allow paths have. Concretely:
return a small record from `DecideBash` — the rung reached, whether it was
*skipped* (disabled) or *refused* (evaluated and said no), and a short cause
string — then let `DenyRung` read that rather than re-deriving from `Default`.

Two properties worth preserving while doing it:

- **`approval_reason` should stay greppable.** A harness filters on it. Keep the
  existing token as the leading word and append the detail in parentheses, so
  `no-match` still matches a prefix and `no-match (Trust=false…)` is strictly more
  information rather than a breaking change.
- **Distinguish *skipped* from *refused*.** "A rung is disabled elsewhere" and
  "your command does not qualify" want different fixes, and the two sibling reports
  are one of each. This distinction is the load-bearing half.

`skipped` vs `refused` also answers the harness question raised at the end of the
trust report: whether a denial blocked a *capability* or merely one *spelling* of
something the agent accomplished another way.

## Scope

Bash is where it costs the most, because the bash ladder has four rungs. The same
shape applies to the path, MCP, fetch and search decisions, which today deny with
no reason channel whatsoever — worth the same treatment, but not the same urgency.

`--resolve`'s canonical-vs-wire gap
([`Resolve_Does_Not_Show_The_Costumed_Wire_Surface.md`](Resolve_Does_Not_Show_The_Costumed_Wire_Surface.md))
is the same *family* — nb declining to show what it already computed — but a
different code path, and it is filed separately on purpose.
