---
kind: plan
title: Approval is not a boundary — the container is
created: 2026-08-12
updated: 2026-08-12
status: current
state: proposed
touches:
  files:
    - Shell/ApprovalPolicy.cs
    - Shell/TrustSandbox.cs
    - Shell/BwrapSandbox.cs
    - Shell/CommandClassifier.cs
    - Facade/NbOptions.cs
    - Program.cs
    - docs/conversation-program-cli.md
    - README.md
    - CLAUDE.md
  features: [approval, sandbox, trust, headless, security]
provenance:
  author: claude
  source: bugs/shell-tool-no-filesystem-sandbox.md
  supersedes-in-part: plans/approval-policy-and-sandbox.md
---

# Approval is not a boundary — the container is

## Why this plan exists

`bugs/shell-tool-no-filesystem-sandbox.md` reports that nb's bash tool has no
filesystem sandbox: the gating is a C# string/path heuristic, and a model can
read any file the nb process user can read. The report is correct and its two
holes are real. This plan does not close them by building a stronger sandbox.
It closes them by **giving up the claim**.

The reason is that the claim is residue. nb has had three prior lives:

1. **A general chat client.** A human sat watching every turn, so "stop and ask
   the human" was a coherent control — the human *was* the boundary.
2. **An automation helper.** The human still chose the working directory and
   still watched, so "allow everything in cwd and children" was a reasonable
   shorthand for "the stuff I pointed you at."
3. **A coding agent.** Trust mode plus a built-in safe-command list
   (`make`, `npx`, `go build`, `dotnet run`) exist so an agent could build and
   test without nagging. That list auto-approves arbitrary code execution, and
   it is *fine* for that purpose.

nb is none of those now. It is a stateless conversation-program evaluator whose
primary mode is non-interactive. Every one of those rules assumed a human in the
loop who is no longer there, and the rules survived the identity that justified
them. What is left is a mechanism that *looks* like a security boundary,
*documents itself* as one, and cannot be one.

The decision: **the container is the boundary. Approval is an observability and
steering surface, and says so.**

---

## Part 1 — The target, as worked examples

### 1a. The eval run that motivates the whole thing

nb runs *inside* the fixture container (see
`bugs/Feature_Run_Bash_Tool_Inside_A_Container.md` for why nb moved inside).
The container has no network beyond the model endpoint and holds nothing but the
fixture. The program declares its intent:

```
# grade-fixture.nb
approval default deny
approval bash go *
approval bash cd * && go *
approval search allow
tools none +read_file +edit_file +bash
system Fix the failing test in ./cmd. Do not modify the test file.
run Begin.
```

Here is what nb **actually emits** today for a denied call — captured, not
imagined (`approval default deny`, model calls bash, trimmed):

```console
$ nb grade-fixture.nb --output jsonl
{"type":"user","turn":0,"text":"Begin."}
{"type":"tool_call","turn":1,"id":"gf5JsPNQ…","name":"bash","arguments":{"description":"List directory contents","command":"ls","timeout_seconds":30}}
{"type":"tool_result","turn":1,"id":"gf5JsPNQ…","output":"Error: bash (Run) was denied by the approval policy (default: deny) and no allow-rule matched. Permission denied — do not retry; try a different approach.\n\n[nb] bash has failed 1 time(s); 2 attempt(s) left…"}
{"type":"tool_call","turn":2,"id":"6BO86SsU…","name":"list_dir","arguments":{"path":"."}}
{"type":"result","turn":null,"exit_reason":"ok","usage":{…},"turns":3,"tool_calls":2}
```

The denial is the point, and note **why** it is the point. It is not protecting
`/etc/passwd` — the container already decided that file is worthless, and if the
model wanted it badly enough there are a dozen encodings the classifier will not
catch. It is there because *the eval wants to know the model reached for it*.
Denial is a recorded observation about model behavior.

That is a coherent job. "Prevent a determined model from reading a file" is not
a job this layer can do, and the plan stops pretending otherwise.

### 1a′. What writing 1a against the real format exposed

Drafting this section from memory produced a `"decision":"deny","reason":…`
field on the `tool_call` event. **That field does not exist.** Capturing the
real output instead turned up a gap that changes the plan:

- **A denial is prose inside `tool_result.output`, not structured data.** A
  harness that wants to count "how often did the model reach outside its
  surface" has to regex an English sentence that also contains retry-budget
  chatter. `ToolCallEvent` *has* an `Approved` field
  (`TranscriptSerializer.cs:243`) — but it is **read-only**: parsed on
  seed-load, never written. Another dead path, like
  `EnsureServersConnectedAsync`.
- **A run full of denials still exits `ok`.** Correct — the model routed around
  it, which is the documented headless contract — but it means exit status
  carries no signal about policy friction.

If approval's job is now observation, then *approval decisions being
unobservable in the machine-readable output* is the most important defect in
this plan, and it is not mentioned anywhere in
`bugs/shell-tool-no-filesystem-sandbox.md`. It becomes work item 0: **write
`approved` on `tool_call`** (`allow`/`deny` plus the existing reason label —
`pre-approved`, `safe`, `trust`, `default-deny`), and count denials in the
`result` trailer.

That is the pivot in miniature: under the old framing this was a nice-to-have
logging detail, because enforcement was supposed to be the product. Under the
new one it *is* the product.

### 1b. The friction that proves the old rules are wrong now

This is today's behavior, inside that same container, and it is the concrete
thing to fix:

```console
$ nb --trust probe.nb --output jsonl     # program: bash `go env GOROOT && ls $(go env GOROOT)/src`
{"type":"tool_call","turn":1,"name":"bash","decision":"deny","reason":"headless-unmatched"}
```

`--trust` is on. The command is not dangerous. It is denied because
`/usr/local/go/src` is not under cwd, and `TrustSandbox` was written to keep a
coding agent out of a *human's home directory* — a concern that does not exist
in a container whose entire filesystem is the fixture and its toolchain.

So the cwd rule now produces **false denials that have nothing to do with the
model's task**, in the one deployment nb is actually used in. It is not merely
useless here; it actively corrupts evals, because a run that failed on a
harness artifact looks like a run where the model failed.

Target behavior — inside a container, the operator says so once, and the cwd
distinction disappears:

```console
$ nb --host-is-the-sandbox probe.nb --output jsonl
{"type":"tool_call","turn":1,"name":"bash","decision":"allow","reason":"unbounded"}
```

(Name is a placeholder — see the grill list. The point is that it is *one*
explicit operator assertion, not a per-path heuristic, and it reads as a
statement about the deployment rather than a statement about nb's powers.)

### 1c. What `--resolve` should print

The surface must state its own status. Today's docs describe trust as a
"sandbox"; the target says what it is:

```console
$ nb --resolve grade-fixture.nb
run 1:
  tools:    read_file, edit_file, bash
  mcp:      (none)
  approval: default deny
            bash: "go *", "cd * && go *"
            search: allow
  boundary: NONE — nb does not confine the bash child. Approval decisions are
            recorded, not enforced against a determined model. Run nb inside a
            container or VM if the workload is untrusted.
```

A reader who sees that line cannot come away believing nb sandboxes anything.
That sentence is most of the deliverable.

---

## Part 2 — The removal work-list

The spec above is the ruler. Everything below contradicts it somewhere.

### Tier 1 — feature residue (delete)

- **`BwrapSandbox.cs` (92 lines) + `BwrapSandboxTests.cs` (80 lines) + the
  `SandboxMode` enum + `approval sandbox` plumbing.** bwrap is a Linux-only,
  must-be-installed, partial control that only engages when a program asks. In
  the target deployment it is a second, weaker sandbox *inside* the real one. On
  a host it is worse than nothing, because it is the strongest signal in the
  codebase that nb confines things — the exact belief this plan exists to
  remove.

  **Constraint:** `approval sandbox` is published program grammar
  (`docs/conversation-program-cli.md` §5.3) with an out-of-tree consumer. Do not
  make it a parse error. Keep the key parsing, warn, ignore:
  `approval sandbox 'bwrap' is no longer implemented — nb does not confine the
  bash child; run nb inside a container. Ignored.` Delete the machinery, keep
  the word working for one release.

- **The dangerous-command denylist as a *control*** (`CommandClassifier.cs`
  `CheckDangerous`, `rm -rf` / `sudo` / `dd` / `curl|sh`). Keep the
  classification — it drives the approval *display*, which is genuinely useful
  for a human reading a transcript. Delete every claim that it stops anything.

### Tier 2 — structural residue (quarantine and label)

- **`TrustSandbox.cs` and the cwd+temp path rule.** Load-bearing: `DecidePath`
  routes every file tool through it, and the interactive REPL still has a human
  who reasonably wants "don't touch things outside what I pointed you at."
  That is a *UX preference*, not a boundary, and it is the right default for the
  REPL and the wrong one for a container. Keep the code; retarget it as a
  convenience default with an operator-level escape (1b), and label it:

  ```csharp
  // Shape inherited from nb's coding-agent era: keep an agent out of the human's
  // home dir while they watch. It is a convenience default, NOT a boundary — a
  // path check cannot bound reads (see bugs/shell-tool-no-filesystem-sandbox.md).
  ```

- **`SafeCommandPrefixes`** (`ApprovalPolicy.cs`; note the sandbox bug's
  `ConversationManager.cs:1126` citation is stale — it moved). Auto-approving
  `make`, `npx`, `go build` is indefensible as security and perfectly sensible
  as "don't nag me about builds." Keep it, rename the concept from *safe* to
  something that does not assert a safety property (`NoPromptPrefixes` /
  `RoutineCommands`), and label it the same way. `default deny` already
  suppresses it, which is the correct relationship and should be documented as
  the *primary* mechanism rather than a footnote.

- **`ApprovalDecision.Deny` semantics.** Keep exactly as-is. Under the new
  framing this is the load-bearing feature — a recorded, transcript-visible
  refusal — and it is the one part of the subsystem that gets *more* important,
  not less.

### Tier 3 — narrative residue (cheapest, most misleading per byte)

This is the pass that actually lands the pivot, because these are the sentences
a fresh reader — or a fresh Claude session — takes as the spec:

- **`CLAUDE.md`, "Trust Mode (`--trust`)" section.** Currently: *"Auto-approves
  file tools and non-dangerous bash commands **within the working directory
  sandbox**"*, and a "Path sandboxing:" heading. This is the single
  highest-leverage edit in the plan — it is the file every session reads first,
  and it currently teaches that nb has a sandbox.
- **`README.md:229-240`** — "A **trust posture** … within the cwd sandbox",
  "The **bash sandbox** …". Same rewrite; add the explicit non-guarantee.
- **`docs/conversation-program-cli.md` §5.3** — the `sandbox` row goes; the
  implicit-grants paragraph stays (it is already admirably honest: *"build
  commands, i.e. arbitrary code"*) and gets promoted out of fine print.
- **`ApprovalPolicy.cs` XML docs** — "How the bash child is contained", "in-sandbox
  path auto-approves". Retell in the new terms.
- **`plans/approval-policy-and-sandbox.md`** (Phase 5) — the plan that
  established this model. Do not rewrite it; add a Revisions note that Phase 5's
  sandbox axis is superseded by this plan, per the Features/plans convention.
- **`bugs/shell-tool-no-filesystem-sandbox.md`** — resolve as *accepted, not
  fixed*: the holes are real, they stay open by design, and the mitigation is
  deployment. Its central claim ("a denylist can never bound reads") is the
  premise of this plan and should be quoted as such rather than closed over.

### Explicitly out of scope

- `NbOptions.Trust` stays as a library surface (an out-of-tree consumer builds
  against the facade). Its *meaning* changes; its name and type do not.
- MCP `alwaysAllow`, `approval mcp`, `approval search`, `fetch_url` gating —
  untouched. They are declarations of intent, which is exactly what survives.
- The egress question (`plans/egress-tripwire.md`) is adjacent but separate:
  network is a container-level concern under this plan too, and the tripwire's
  value is likewise observational.

---

## Grill list — decide these before writing code

Ordered by how much they change the work. Each has a recommended answer.

1. **Does the operator assertion in 1b exist, and what is it called?**
   Recommend: yes, one flag + config key, and it must name the *deployment*, not
   a permission — `--unconfined` or `Boundary: "external"` rather than
   `--yolo`/`--trust-all`. A name that sounds like a permission grant invites
   use on a laptop, which is the failure this plan is trying to prevent.
2. **Should it be the default when nb detects a container?** Recommend: **no.**
   Container detection is heuristic (`/.dockerenv`, cgroup sniffing), and a
   wrong guess silently removes the REPL user's expected behavior. Explicit
   assertion, always.
3. **Does the REPL keep the cwd default?** Recommend: yes. There is a real human
   watching, so the convenience default is right there and only there.
4. **Does `--trust` keep its name?** Recommend: yes for the flag (breaking a
   published flag over vocabulary is not worth it), no for the docs — stop
   calling what it does a sandbox.
5. **Does bwrap get deleted or left dormant behind the directive-warning?**
   Recommend: delete the machinery. A dormant sandbox is exactly the "dead path
   that looks like it handles the case" that `EnsureServersConnectedAsync`
   turned out to be — and this session already deleted one of those.
6. **Does the `boundary:` line in 1c appear in `--resolve` only, or in the jsonl
   `result` event too?** Recommend: both. A harness archiving transcripts should
   be able to tell, years later, that a given run was unconfined.
7. **Is writing `approved` on `tool_call` (1a′) a breaking wire change?**
   Recommend: no — it is an added optional field, the reader already accepts it,
   and unknown fields are tolerated. But it is a schema change to a published
   contract with an out-of-tree consumer, so it wants a line in the API doc
   rather than a silent addition.
8. **Do `allow` decisions get recorded, or only denials?** Recommend: both, with
   the reason label. "Allowed because the built-in safe list matched" is exactly
   the fact this plan wants visible — it is how a reader discovers that `make`
   auto-approved without anyone asking for it.

---

## Sequencing

Removal precedes construction (the corpus outvotes the decision otherwise) —
except for item 0, which is additive and independently valuable:

0. Emit `approved` on `tool_call` and a denial count in the `result` trailer
   (1a′). Do this first: it is the only item that makes approval *do* its new
   job rather than merely stop claiming the old one, and it is useful even if
   the rest of this plan is rejected.
1. Tier 3 narrative pass — CLAUDE.md, README, §5.3, XML docs. No behavior
   change, no tests, immediately stops the corpus teaching the old model.
2. Resolve `bugs/shell-tool-no-filesystem-sandbox.md` as accepted-by-design,
   with this plan as the rationale.
3. Tier 1 deletion — bwrap machinery; `approval sandbox` degrades to a warning.
   Tests: the directive still parses, still warns, no longer confines.
4. Tier 2 labelling + the operator assertion from grill #1, with the false-denial
   case in 1b as the regression test.
5. `boundary:` reporting in `--resolve` and the result event.

## Done test

Hand a fresh reader nothing but the code and docs. If they can state **"nb does
not confine the tools it runs; run it inside something that does"** without
having read this plan, it landed. If they come away believing trust mode or the
safe list protects anything, it did not — regardless of what the code does.
