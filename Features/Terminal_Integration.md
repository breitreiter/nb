# nb Terminal Integration Proposal

Status: Needs Work

## Overview

Add a native shell execution tool to nb that provides filesystem and shell access. This enables use cases like AI-assisted troubleshooting, where the model can run diagnostic commands and analyze results without tedious copy-paste loops.

Implemented as a native tool (not MCP) so nb has full control over approval UX - specifically, showing the actual command to be executed rather than just a tool name.

## Shortcomings and Opportunities for Improvement

Right now, the command approval process is pretty clunky. It was borrowed uncritically from MCP approvals, where it was adequate to handle occasional calls to tools.

This is falling apart now, because:
- In terminal-connected conversations, it's not unusual for a single conversation turn to include 4-8 tool calls. Approving each of these is tedious.
- The UX for calls and approvals is not great. Each call is four lines: intent, literal shell command, approval, exit code. This is hard to scan and creates huge blocks of noise in the conversation history. All the information is valuable, but the presentation needs love.
- Currently we eat the output of the command. This is fine when the user doesn't care or understand what the model is doing. However, if the user wishes to be an active participant, there is no way to know what information the model is acting on.

In a GUI, we could use tricks like progressive disclosure to selectively show/hide this information. We would also have access to typographical treatments to establish information hierarchies. In the console world, we have:
- Font color. On the plus side, CLI users are a bit more open to vibrant colors, so color can actually be pretty powerful here.
- Indentation and box-drawing characters. Given the tight viewport we're working with, we need to use these features very cautiously. 
- Ephemeral content. This includes classic backspace-animation, but also more sophisticated ncurses-style presentations.

## System Prompt Context

Inject machine context into the system prompt so the model can make informed decisions.

### Static Context (set once at startup)

Detected at nb startup and included in every system prompt:

```
## Environment
- OS: Linux (Ubuntu 22.04) / macOS 14.1 / Windows 11
- Shell: bash / zsh / powershell
- Architecture: x86_64 / arm64
- User: joseph
- Home: /home/joseph
- Case-sensitive filesystem: yes / no
```

### Capability Detection

Check for common utilities at startup and report availability. This shapes how the model approaches problems - it won't suggest ffmpeg commands if ffmpeg isn't installed.

```
## Available Tools
Present: python3, dotnet, git, docker, ffmpeg, curl, jq
Not found: node, ruby, kubectl, aws
```

Detection: run `which <tool>` (Unix) or `where <tool>` (Windows) for a configured list of utilities. List is user-configurable in settings with sensible defaults.

Default detection list:
```json
{
  "terminalIntegration": {
    "detectTools": [
      "python3", "python", "node", "dotnet", "ruby",
      "git", "docker", "kubectl",
      "ffmpeg", "magick", "curl", "jq", "aws", "az", "gcloud"
    ]
  }
}
```

### Dynamic Context

**Current working directory** - changes during session as the model navigates.

Options:
1. Include in system prompt (uses tokens every request, but always visible)
2. Expose via `get_cwd` tool the model can call when needed

Recommend option 1 for cwd since it's small and frequently relevant:
```
## Session
- Working directory: /home/joseph/projects/myapp
```

## Working Directory Handling

**Problem:** Each shell invocation is independent. If the model runs `cd /tmp` in one tool call, it won't persist to the next. Models expect cd to work and will get confused.

**Complication:** nb stores conversation history in `.nb_conversation_history.json` relative to where nb was launched. If the model "changes directory" and that affects history location, we'd lose conversation context or write history to unexpected places.

### Two Directory Concepts

Separate the concerns:

```
launchDirectory  - where nb was started, where history lives (immutable for session)
shellCwd         - where shell commands execute (mutable by model)
```

At startup:
```csharp
var launchDirectory = Directory.GetCurrentDirectory();
var shellCwd = launchDirectory; // starts the same
var historyPath = Path.Combine(launchDirectory, ".nb_conversation_history.json");
```

When model changes directory:
- Update `shellCwd` only
- History path unchanged (anchored to `launchDirectory`)
- System prompt shows `shellCwd` as the working directory
- Shell commands execute in `shellCwd`

Example session:
1. User launches nb in `/home/joseph/myproject`
2. Model runs `cd /tmp` to do some work
3. `shellCwd` becomes `/tmp`, subsequent commands run there
4. History still saves to `/home/joseph/myproject/.nb_conversation_history.json`

The history file location reflects "which project am I working on" (user's choice at launch). The shell cwd reflects "where is the model currently poking around" (model's choice during session).

### Tracking CWD Changes

**Option A: Explicit cwd parameter only (simpler)**

- `run_cmd(command, cwd?)` - cwd defaults to current `shellCwd`
- Add a `set_cwd(path)` tool for explicit directory changes
- No parsing of commands - model explicitly requests cwd changes
- Clean and predictable

**Option B: Parse cd commands (more natural)**

Track `shellCwd` by detecting cd in commands. Edge cases to handle:
- Standalone `cd /path` - update shellCwd
- Compound `cd /tmp && ls` - update shellCwd to /tmp
- Subshells `(cd /tmp; ls)` - don't update (ran in subshell)
- Conditionals `[ -d /foo ] && cd /foo` - only update if cd actually ran (check exit code? fragile)
- Failed cd - don't update if exit code non-zero
- `pushd`/`popd` - would need a stack

Recommendation: Start with Option A. Add cd-parsing later if the explicit approach feels awkward in practice.

## Tool Design

### `run_cmd`

Execute a shell command and return results.

```csharp
ToolResult RunCommand(string command, string? cwd = null, int? timeoutSeconds = null)
```

Parameters:
- `command` - the shell command to execute
- `cwd` - working directory (defaults to nb's cwd)
- `timeoutSeconds` - max execution time (default: 30)

Returns:
```json
{
  "stdout": "...",
  "stderr": "...",
  "exitCode": 0
}
```

Implementation notes:
- Unix: `/bin/bash -c "command"` (or user's configured shell)
- Windows: `cmd.exe /c "command"` or `powershell -Command "command"`
- Capture both stdout and stderr separately
- Kill process if timeout exceeded (return partial output + "[Killed - exceeded Ns timeout]")
- Assume UTF-8 encoding; if output isn't valid UTF-8, return error: "Binary output detected. Use /insert for binary files."

### Output Size Handling

Large output wastes context and doesn't give the model feedback to try a smarter approach. Use a "sandwich" strategy:

- **Small output** (< threshold): return everything
- **Large output** (> threshold): return first 50 lines + last 20 lines + stats

```
Dec 29 00:00:01 server CRON[1234]: ...
Dec 29 00:00:02 server systemd[1]: ...
(... first 50 lines ...)

[... 44,900 lines omitted (2.3MB total) - use grep/tail/head to filter ...]

Dec 29 23:58:01 server nginx[5678]: ...
Dec 29 23:59:59 server kernel: ...
(... last 20 lines ...)
```

This mimics human behavior: see wall of text, Ctrl+C, run `head` and `tail` to understand structure before grepping. We do it automatically, saving a round-trip.

Threshold: ~10KB or ~200 lines, whichever hits first. Configurable.

### Why No Separate File Tools?

Models have strong priors about shell commands from training - they know `cat` reads files and `echo >` writes them. Custom tools like `read_file` or `write_file` fight against this training and can cause confusion (the OpenAI Codex team noted models get confused when given tools with familiar names but different behavior - the inverse is also true).

Instead: let the model use shell commands naturally, and pattern-match in the approval layer to show clean prompts. See "Approval UX" section below.

## Approval UX

Native tool implementation allows custom approval display. Pattern-match commands to show user-friendly prompts.

### Command Classification

Detect common operations and display clean prompts:

| Pattern | Display |
|---------|---------|
| `cat <path>` | `read: <path>` |
| `head <path>`, `tail <path>` | `read: <path>` |
| `echo ... > <path>` | `write: <path>` |
| `echo ... >> <path>` | `append: <path>` |
| `cp <src> <dst>` | `copy: <src> → <dst>` |
| `mv <src> <dst>` | `move: <src> → <dst>` |
| `rm <path>` | `delete: <path>` |
| `mkdir <path>` | `mkdir: <path>` |
| (unrecognized) | `run: <command>` |

Examples:
```
read: /etc/hosts
[Y]es  [N]o  [A]lways  [?]

write: ~/.bashrc
[y]es  [N]o  [?] show content

run: yarn cache clean && yarn install
[Y]es  [N]o  [A]lways  [?]
```

Truncate long commands with `?` to show full:
```
run: find /var/log -name "*.log" -mtime +7 -exec rm...
[Y]es  [N]o  [A]lways  [?] show full
```

### Dangerous Command Warnings

Pattern-match against risky commands and add visual warning + flip default to No:

```
run ⚠️: sudo rm -rf /tmp/cache
[y]es  [N]o  [?]
```

Warning patterns (configurable):
- `rm -rf`, `rm -r` (recursive delete)
- `sudo` (privilege escalation)
- `dd` (disk operations)
- `> /` or `>> /` (redirect to root paths)
- `chmod 777`, `chmod -R` (permission changes)
- `curl | sh`, `wget | sh` (pipe to shell)
- `mkfs`, `fdisk` (disk formatting)

Additionally, all write/append/delete/move operations show warning by default:
```
write ⚠️: ~/.bashrc
[y]es  [N]o  [?] show content

delete ⚠️: /tmp/cache/*
[y]es  [N]o  [?]
```

For warned commands:
- Default changes from Yes to No
- "Always" option hidden (must approve each time)

### Multi-Line Scripts

Models naturally emit multi-line scripts for related operations. Allow this - the script is the batching mechanism.

```
run (4 lines):
  mkdir -p /tmp/build
  cd /tmp/build
  cmake ..
  make -j4
[Y]es  [N]o  [?]
```

Display rules:
- Show entire script (no truncation - scrolling is fine)
- Scan all lines for dangerous patterns
- One warning covers the whole script if any line matches
- Single approval for the whole thing

```
run ⚠️ (6 lines):
  cd /var/log
  find . -name "*.log" -mtime +30 -delete
  rm -rf /tmp/cache
  systemctl restart nginx
  echo "done"
[y]es  [N]o  [?]
```

### Auto-Approval for Scripting/Testing

For automated testing or scripting scenarios, allow pre-approving commands via command line:

```bash
# Pre-approve specific commands for this invocation
./nb --approve "ls" --approve "cat *" "how many files are here?"

# Multiple approvals
./nb --approve "ls" --approve "pwd" --approve "cat package.json" "analyze this project"
```

Matching rules:
- Exact match: `--approve "ls"` approves only `ls`
- Glob patterns: `--approve "cat *"` approves `cat` with any arguments
- Multiple `--approve` flags accumulate

Commands matching an `--approve` pattern skip the interactive prompt entirely. Non-matching commands still require approval.

This enables:
- Automated testing of nb itself
- Scripting with predictable tool usage
- CI/CD pipelines that use nb

## Implementation Sequence

1. **Basic run_cmd** - Execute commands in cwd, return stdout/stderr/exit. Simple approval showing command.

2. **System prompt context** - Inject OS, shell, user, home, architecture, cwd.

3. **Capability detection** - Check for configured tools at startup, include in system prompt.

4. **Command classification** - Pattern-match commands to show clean approval prompts (read/write/delete vs raw command).

5. **Dangerous command warnings** - Pattern matching, visual indicators, flip defaults.

6. **Output limits** - Truncation for large outputs, timeout handling.

## Configuration

```json
{
  "terminalIntegration": {
    "enabled": true,
    "shell": "auto",
    "defaultTimeout": 30,
    "outputThresholdBytes": 10240,
    "outputThresholdLines": 200,
    "sandwichHeadLines": 50,
    "sandwichTailLines": 20,
    "detectTools": ["python3", "node", "dotnet", "git", "docker", "..."],
    "warnPatterns": ["rm -rf", "sudo ", "dd ", "..."]
  }
}
```

## Open Questions

None - all resolved.
