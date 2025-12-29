# nb Terminal Integration Proposal

## Overview

Replace nb's basic test MCP server (echo, string-reverse) with an OS integration server that provides filesystem and shell access. This enables use cases like AI-assisted troubleshooting, where the model can run diagnostic commands and analyze results without tedious copy-paste loops.

## System Prompt Context

The MCP server should expose context that helps the model make good decisions. Include in system prompt (or via MCP resources):

**Static (set once at startup):**
- OS name
- Shell name (e.g., bash, zsh, powershell)
- Username
- Home directory path
- Platform architecture (x86_64, arm64)
- Case sensitivity (filesystem behavior differs across platforms)

**Dynamic (may change during session):**
- Current working directory

For dynamic context, consider whether to include in system prompt (uses tokens every request) or make available via a `get_context` tool the model can call when needed.

Optional/nice-to-have:
- PATH (helps model know what commands are available)
- Terminal capabilities (color support, dimensions)
- Default editor

## Working Directory Handling

**Problem:** Each shell invocation is independent. If the model runs `cd /tmp` in one tool call, it won't persist to the next. Models expect cd to work and will get confused.

**Recommended approach:** Track cwd in nb's state.

- Shell tool accepts optional `cwd` parameter, defaults to nb's tracked cwd
- When model runs `cd /somewhere`, parse and update nb's internal cwd state
- All subsequent shell calls use the updated cwd
- Need to handle: relative paths, `cd -`, `cd ~`, validation that target exists

**Alternative:** Structured tools with explicit cwd.

Instead of a raw shell tool, expose specific operations:
- `read_file(path)`
- `write_file(path, content)`
- `list_directory(path)`
- `execute_command(command, cwd)`

Each command specifies its working directory explicitly. Model can't "cd" at all - must be explicit per-command. Cleaner from an MCP design perspective but less natural for the model.

**Not recommended for initial implementation:** Persistent shell sessions. This is essentially building a terminal emulator (PTY management, prompt detection, output parsing, timeout handling, interactive input). Powerful but complex. Only pursue if the simpler approaches prove insufficient.

## Tool Design

Minimum viable set:
- `run_cmd` - execute a shell command, return stdout/stderr/exit code
- `read_file` - read file contents
- `write_file` - write/overwrite file
- `list_dir` - directory listing

Consider also:
- `get_context` - returns current cwd, env vars, etc.
- `file_exists` / `path_info` - check existence, get metadata

For `run_cmd`:
- Accept optional `cwd` parameter
- Return structured result: `{ stdout, stderr, exitCode }`
- Consider timeout parameter with sensible default

## Approval Mechanism

**Problem:** nb's current approval is at the tool name level (`os_run_cmd? [Y/n/a/?]`). This doesn't work for shell tools where the risk is in the arguments, not the tool name. Approving "always" for `run_cmd` is dangerous.

**Current nb approval flow:**
- Terse display: `toolname? [Y/n/a/?]`
- Options: yes, no, always, show full request
- Full request hidden by default (could be verbose, e.g., file writes)

**Proposed enhancement:** Tool-specific approval formatting via server config.

Add to MCP server config in nb:

```json
{
  "mcpServers": {
    "os": {
      "command": "...",
      "approvalHints": {
        "run_cmd": {
          "displayArgs": ["command"],
          "truncate": 60,
          "warnPatterns": ["rm ", "sudo ", "dd ", "> ", "| ", "curl "]
        },
        "write_file": {
          "displayArgs": ["path"],
          "alwaysWarn": true
        },
        "read_file": {
          "displayArgs": ["path"]
        }
      }
    }
  }
}
```

Behavior:
- `displayArgs`: which tool arguments to show inline in the terse prompt
- `truncate`: max chars before truncating (user can hit `?` for full)
- `warnPatterns`: if command matches any pattern, add visual indicator (⚠️) and maybe require explicit `y` instead of default-yes
- `alwaysWarn`: always show warning indicator

Example approval prompts with this config:
```
os_run_cmd: yarn cache clean && yarn install? [Y/n/a/?]
os_run_cmd ⚠️: sudo rm -rf /tmp/cac...? [y/N/a/?]
os_write_file ⚠️: /home/joseph/.bashrc? [y/N/?]
```

Note: dangerous operations might flip default to No and/or hide the "always" option.

**Alternative considered:** Server-provided metadata via `x-nb-*` extensions in tool definitions. Cleaner architecturally (server knows its tools) but more complex. Start with config-based approach since server configs already exist in nb.

## Implementation Sequence

Suggested order:

1. **Basic MCP server with shell tool** - `run_cmd` that executes in cwd, returns stdout/stderr/exit. No state tracking yet.

2. **System prompt context** - Inject OS, shell, user, home, cwd into system prompt.

3. **Approval formatting** - Add `approvalHints` config support. Show command strings in approval prompt.

4. **CWD tracking** - Parse cd commands, maintain state in nb, pass cwd to shell invocations.

5. **Additional tools** - `read_file`, `write_file`, `list_dir` as needed.

6. **Warn patterns** - Add visual indicators for risky commands, flip defaults.

## Open Questions

- Should `read_file` / `write_file` be separate tools, or just let the model use shell commands (`cat`, redirection)? Separate tools are more explicit and easier to control approval for.

- How to handle long-running commands? Timeout with error, or stream output somehow?

- Should there be a "batch approval" flow where model proposes multiple commands and user approves the set? Useful for diagnostic sequences but adds UX complexity.
