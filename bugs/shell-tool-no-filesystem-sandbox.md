---
kind: bug
title: 'bash tool has no filesystem sandbox — model can read arbitrary files'
created: 2026-07-06
updated: 2026-09-05
status: current
state: wontfix
severity: high
cluster: bash-boundary
---

# bash tool has no filesystem sandbox — model can read arbitrary files

Status: **Closed 2026-09-05 — accepted by design, not fixed.** Both holes are
real, both remain open, and nb will not close them. See *Resolution* at the foot
of this report. The analysis below stands unaltered: it is the premise of
`plans/approval-is-not-a-boundary.md`, not a defect list that plan discharges.

Originally: Reported (2026-07-06) — unpatched. Verified against current source.

> **Update 2026-08-12 — Hole #2's repro works now, and did not before.**
> Until today the unsandboxed bash path escaped `$` and backticks
> (`BashTool.EscapeBash`), so `echo $(cat /etc/passwd)` was a bash *syntax
> error* and backticks arrived literal — command substitution could not execute
> unsandboxed, and Hole #2's repro would have failed if anyone had run it. That
> was a side effect of wrapper quoting, not a control, and it came at the cost of
> corrupting every `$` inside single quotes
> (`bugs/Bash_Escapes_Dollar_Inside_Single_Quotes.md`).
>
> That escaping is gone as of that fix — bash now receives the command verbatim.
> **Hole #2 is live exactly as described below**, and its severity assessment
> stands unchanged. Gating `$(...)` in `CommandClassifier` was deliberately not
> done as part of that fix: it belongs to this report, and a denylist still
> cannot bound reads, which is this report's core argument.

One-line summary: nb's `bash` tool runs the model's command string through
`bash -c` with **no OS-level isolation**. The only gating is a C# string/path
heuristic that is not a security boundary. A model driving the tool can read any
file the nb process user can read — and one path (Hole #2) needs no `--trust`.

Severity: **High.** Under `--trust` a model reads arbitrary absolute paths with
no prompt (Hole #1). **Worse: Hole #2 leaks arbitrary file contents on a plain
`nb` invocation with no trust flag at all**, via command substitution inside an
auto-approved prefix.

---

## Threat model

The bash tool is driven by the model, not the human. The human approves (or, in
trust mode, pre-approves a class of) commands. The security question is: *can a
misaligned or prompt-injected model read files outside the working directory
without the human noticing?* Today the answer is yes.

There is **no OS sandbox anywhere in the codebase** — confirmed by grep: no
`bwrap`, `firejail`, `unshare`, `seccomp`, `chroot`, or mount-namespace call.
The child process is spawned by a bare `ProcessStartInfo` in
`Shell/BashTool.cs:76-90` (`GetShellCommand`, `Shell/BashTool.cs:216-220`), with
`WorkingDirectory` set to `_env.ShellCwd` — which is just
`Directory.GetCurrentDirectory()` at launch (`Shell/ShellEnvironment.cs:40,55`).
No environment scrubbing. The cwd only affects *relative* paths; absolute paths
bypass it entirely.

The tool is registered in `Shell/BashTool.cs:41-63` and wired into the model's
toolset at `ConversationManager.cs:196-198`. The approval gate for every bash
call is `HandleBashToolCall` (`ConversationManager.cs:1018-1122`). Its
auto-approval order:

1. `--approve` pattern match — `ConversationManager.cs:1026`
2. `!IsDangerous && IsSafeCommand(command)` — `ConversationManager.cs:1033`
3. `_trustMode && !IsDangerous && IsBashCommandTrusted(...)` —
   `ConversationManager.cs:1040-1049`
4. else an interactive prompt — `ConversationManager.cs:1080-1115`

Both holes below live in steps 2 and 3.

---

## Hole #1 — under `--trust`, the `Run` category is unconditionally trusted

`--trust` is parsed at `Program.cs:232-234` (also settable via config
`"Trust": true`, `Program.cs:349-354`).

`IsBashCommandTrusted` (`ConversationManager.cs:1160-1195`) short-circuits to
`return true` for **any** `Run`-category command
(`ConversationManager.cs:1172-1173`) — no path check at all. The classifier
(`Shell/CommandClassifier.cs`) only tags a command `Read` for the five commands
`cat / head / tail / less / more` (`Shell/CommandClassifier.cs:169`); every other
single-line command falls through to the `Run` default
(`Shell/CommandClassifier.cs:159`).

So under `--trust`, all of these auto-run with **no prompt and no path check**:

```
grep secret /home/user/.ssh/id_rsa
find / -name id_rsa
awk '{print}' /etc/passwd
sed -n 1,5p /etc/passwd
python3 -c 'print(open("/etc/passwd").read())'
sort ~/.aws/credentials
xxd ~/.ssh/id_rsa
strings /home/user/.gnupg/secring.gpg
od -c /etc/shadow
nl ~/.aws/credentials
cut -d: -f1 /etc/passwd
tr a-z A-Z < ~/.ssh/id_rsa
```

Only `cat / head / tail / less / more` reach the `TrustSandbox` path check
(`ConversationManager.cs:1180-1185`). So `cat /etc/passwd` prompts — but
`sed -n 1,5p /etc/passwd` or `grep . /etc/passwd` on the *same file* does not.
The read-command denylist is a five-item allowlist trivially sidestepped by the
hundreds of other read-capable binaries.

(Note: `cp` and `mv` are classified `Copy`/`Move` and *are* path-checked via
`TrustSandbox`, so `cp /etc/passwd .` does prompt — that path is not the hole.
The hole is the unconditional `Run` trust.)

### Repro (Hole #1)

```
nb --trust
> (prompt the model to) run:  sed -n '1,20p' /etc/passwd
```

Expected: a prompt, or refusal for an out-of-cwd read. Actual: auto-runs, prints
the file, logged only as `• auto: bash …`.

---

## Hole #2 — NO `--trust` required: safe-prefix list + command substitution

This is the worse of the two: **no trust flag needed.**

`SafeCommandPrefixes` (`ConversationManager.cs:1126-1145`) auto-approves a
command by first-token prefix with **zero path check** — the list includes
`echo`, `env`, `wc`, `file`, `du`, `df`, `ls`, `pwd`, `which`, `whereis`, `date`,
`uname`, `whoami`. `IsSafeCommand` (`ConversationManager.cs:1147-1158`) matches on
prefix + a delimiter, nothing more.

`CheckDangerous` (`Shell/CommandClassifier.cs:215-238`) does **not** flag command
substitution — `$(...)` / backticks are not in the denylist
(`Shell/CommandClassifier.cs:24-44`, which is entirely write/destruction
patterns). So the shell expands the substitution while the classifier only ever
saw the outer `echo`.

Result — arbitrary read on a plain `nb`, no trust flag:

```
echo $(cat /etc/passwd)          # matches "echo " prefix → not dangerous → auto-runs, prints the file
echo "$(cat ~/.ssh/id_rsa)"
env X=$(cat ~/.aws/credentials)  # "env" prefix
```

Metadata leaks unprompted the same way:

```
wc -l ~/.ssh/id_rsa
file ~/secret.key
du -a /home
```

### Repro (Hole #2)

```
nb                                # no flags
> (prompt the model to) run:  echo $(cat /etc/passwd)
```

Expected: a prompt. Actual: auto-approved (`• bash: …`), file contents printed to
the model.

---

## Why the existing mechanisms don't cover this

- **`TrustSandbox`** (`Shell/TrustSandbox.cs`) is a string-prefix path test
  (`IsUnderDirectory`, `Shell/TrustSandbox.cs:107-115`) that additionally
  whitelists the entire system temp dir (`IsUnderTempDirectory`,
  `Shell/TrustSandbox.cs:84-105`). It is consulted only by the native
  `read_file / grep / find / list` tools and by the five `cat`-family bash
  commands — **never for `Run`, and never in Hole #2's path at all**.
- **The "dangerous command" denylist** (`Shell/CommandClassifier.cs:24-44`:
  `rm -r`, `sudo`, `dd`, `chmod`, `curl|sh`, redirects to `/dev|/etc|/usr|/bin`)
  is entirely write/destruction-oriented. Nothing about reads or exfiltration,
  and nothing about command substitution.
- **Per-cwd trust/history scoping** only governs where nb writes its own state.
  It is not a read boundary.

The core problem: a denylist can never bound reads. If `cat`/`grep`/`find` were
blocked, `head` / `awk` / `python -c` / `od` / redirection / hundreds of other
binaries still read files. The only guarantee that holds against an untrusted
model is an **OS-enforced** sandbox.

---

## Fix — ranked by effort vs. strength

**Recommended: option 1 (bwrap).** It is the smallest localized change, it is
kernel-enforced, and it is the only option below that actually holds against a
model that can run arbitrary commands.

1. **Wrap the child in `bwrap`** at the spawn point (`Shell/BashTool.cs:216-220`,
   where `GetShellCommand` builds the `bash -c` invocation). e.g.
   `bwrap --ro-bind / / --tmpfs <mask ~/.ssh, ~/.aws, /etc secrets…>
   --bind <cwd> <cwd> --chdir <cwd> --unshare-all [--share-net if the tool needs
   it] -- bash -c "…"`. Kernel-enforced, one change point. **The only option
   that actually holds.**
2. **`firejail --private=<cwd>`** at the same point — simpler flags, requires
   firejail installed.
3. **Run nb as a throwaway unprivileged user** whose only readable dir is the
   workdir — no code change, but coarser and operationally heavier.
4. **Mount-namespace / chroot via `unshare -m`** — real isolation, more moving
   parts to get right.
5. **Tighten the C# classifier** (path-check `Run`, flag command substitution,
   extend denylists) — **explicitly NOT a security boundary for an untrusted
   model.** Any denylist of read commands is bypassable. Worth doing only as
   defense-in-depth / UX, never as the guarantee.

---

## Key anchors for a fixer

- Unconditional `Run` trust (Hole #1): `ConversationManager.cs:1172-1173`
  (`if (classified.Category == CommandCategory.Run) return true;`), inside
  `IsBashCommandTrusted` (`ConversationManager.cs:1160-1195`).
- Safe-prefix auto-approve + command-substitution gap (Hole #2):
  `IsSafeCommand` at the call site `ConversationManager.cs:1033`, the prefix set
  `ConversationManager.cs:1126-1145`, and the missing substitution check in
  `CheckDangerous` (`Shell/CommandClassifier.cs:215-238`).
- The five `cat`-family reads that *are* checked (and everything else that
  isn't): `Shell/CommandClassifier.cs:169` (Read set) vs.
  `Shell/CommandClassifier.cs:159` (Run default).
- **Process-spawn point to wrap:** `Shell/BashTool.cs:216-220` (`GetShellCommand`)
  feeding `ProcessStartInfo` at `Shell/BashTool.cs:76-90`. Cwd origin:
  `Shell/ShellEnvironment.cs:40,55`.

---

## Resolution — accepted by design (2026-09-05)

**This report is correct, its two holes are live, and nb is not going to fix
them.** It closes as *accepted*, not *fixed*, because the thing being given up is
the claim rather than the capability. Rationale:
`plans/approval-is-not-a-boundary.md` (accepted 2026-09-05), whose threat model
section is the doc this closes against.

### What is accepted

This report's central claim is the premise the plan is built on, and it is quoted
there rather than closed over:

> The core problem: a denylist can never bound reads. If `cat`/`grep`/`find` were
> blocked, `head` / `awk` / `python -c` / `od` / redirection / hundreds of other
> binaries still read files. The only guarantee that holds against an untrusted
> model is an **OS-enforced** sandbox.

That is right, and it is why no amount of work on `CommandClassifier`,
`TrustSandbox` or `SafeCommandPrefixes` can resolve this. The conclusion the plan
draws is that nb should stop being the layer that tries.

Hole #1 (unconditional `Run` trust) and Hole #2 (safe-prefix + command
substitution, no trust flag required) both remain reachable. Anyone relying on
nb's approval ladder to bound reads is relying on something that does not work.

### What replaces it

**One container. nb runs inside it. One filesystem.** The container is the
boundary; approval is an observability and steering surface that records what the
model reached for. A denial is a datum about model behaviour, not a control.

The mitigation for both holes is therefore deployment: run nb inside a disposable
container that holds the fixture and nothing you would mind the agent reading.

### Why not the fix this report recommends

The report ranks **bwrap first**. That ranking is superseded, and bwrap is being
*removed* rather than extended. Two reasons:

1. **It would sit inside the real boundary.** Under the accepted topology nb is
   already in a container, so bwrap is a second, weaker, Linux-only, partial
   sandbox nested in a working one.
2. **It is the strongest signal in the codebase that nb confines things** — the
   precise belief that makes an operator run an adversarial workload on their
   laptop. A dormant partial sandbox is worse than none.

The alternative that briefly replaced it — route bash into a container with nb
outside (`plans/container-bash-exec.md`) — is also rejected, for a reason
specific to nb's architecture and worth stating here so it is not re-proposed:
`read_file`, `write_file`, `edit_file`, `list_dir`, `find_files` and `grep` are
in-process .NET and never route through bash. Confining only bash gives the agent
**two filesystems** — host paths from `read_file`, container paths from a build,
`cd` a no-op between them.

### The residue this leaves, stated rather than hidden

Under nb-inside, two properties follow that no reader should have to discover:

- **nb shares a filesystem with the agent under test.** `appsettings.json`, seed
  transcripts and nb's binaries are readable by the thing being tricked. The
  container must hold the fixture and nothing else of value.
- **nb shares a network namespace with it.** Whatever nb can reach, the agent can
  reach — so the egress allowlist is the union, and wants to be as small as nb's
  own needs allow.

### Where the work went

Not here. The plan's revised work list carries it: the narrative pass that stops
the corpus teaching that nb sandboxes, a `boundary` directive that declares the
deployment in-band, `boundary:` reporting in `--resolve` and the result trailer,
and a reference container topology shipped in-tree so the standard shape is
something to copy rather than tribal knowledge.

**Do not reopen this to build a sandbox.** Reopen it only if nb's deployment model
changes such that it can no longer run inside the thing that confines it.
