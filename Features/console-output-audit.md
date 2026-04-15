# Console Output Audit

## Taxonomy

### 1. Banners & Chrome — App identity, session framing
| Where | What |
|-------|------|
| `Program.cs:287-294` | ASCII robot + provider/MCP/skills/version banner on startup |
| `Program.cs:300,308` | Decorative divider lines (🞌 character) between turns |
| `Program.cs:302` | `You:` prompt prefix |

### 2. User Input — Reading from the user
| Where | What |
|-------|------|
| `Program.cs:304` | `Console.ReadLine()` — main interactive input |
| `Program.cs:245-247` | Piped stdin detection + read |
| `ConversationManager.cs:437,650` | `Console.ReadKey()` — single-key approval (y/n) |
| `ConversationManager.cs:461,656` | `AnsiConsole.Prompt()` — rejection reason text |
| `ConversationManager.cs:446-448` | `AnsiConsole.Confirm()` — tool call confirmation |
| `Program.cs:332-333` | `AnsiConsole.Confirm()` — skill match suggestion |
| `PromptProcessor.cs:51` | `AnsiConsole.Ask<string>()` — MCP prompt parameter input |

### 3. Tool Approval UX — The approve/reject flow for tool calls
| Where | What |
|-------|------|
| `ConversationManager.cs:623-634` | Bash: show description, category, command text, danger warning |
| `ConversationManager.cs:649-651` | Bash: "Execute? [y/n/s]" prompt + key read |
| `ConversationManager.cs:604,615` | Bash: pre-approved / trust-mode auto-approved status |
| `ConversationManager.cs:666,830,925` | Rejected messages (bash, write, edit) |
| `ConversationManager.cs:812-825` | Write file: path, size, approval prompt |
| `ConversationManager.cs:836-838` | Write file: content preview on `s` key |
| `ConversationManager.cs:902-921` | Edit file: old/new text, replace-all flag, approval prompt |
| `ConversationManager.cs:436-437` | Generic MCP tool: approval prompt + key read |
| `ConversationManager.cs:489` | Trust-mode auto-approved (generic tool) |
| `ConversationManager.cs:793,880` | Write/edit auto-approved in trust mode |

### 4. Tool Execution Status — What the model is doing
| Where | What |
|-------|------|
| `ConversationManager.cs:226` | "Calling tool: X" |
| `ConversationManager.cs:268,274` | Read file: path, line count |
| `ConversationManager.cs:307,313` | Find files: pattern, result count |
| `ConversationManager.cs:338,344` | Grep: pattern, match count |
| `ConversationManager.cs:380,384` | MCP tool: call + result summary |
| `ConversationManager.cs:769` | Bash: exit code + pass/fail |
| `ConversationManager.cs:801,806` | Write file: success/error |
| `ConversationManager.cs:853,858` | Write file: success/error (after approval) |
| `ConversationManager.cs:891,896` | Edit file: success/error |
| `ConversationManager.cs:940,945` | Edit file: success/error (after approval) |
| `ConversationManager.cs:410-414` | Fake tool invocation display |

### 5. LLM Response — The actual model output
| Where | What |
|-------|------|
| `ConversationManager.cs:591` | Rendered markdown response |
| `ConversationManager.cs:571-581` | Spinner animation while waiting |

### 6. Errors — Something went wrong
| Where | What |
|-------|------|
| `ConversationManager.cs:238,245` | Tool call errors |
| `ConversationManager.cs:279,318,349` | File tool errors (read, find, grep) |
| `ConversationManager.cs:391` | MCP tool error |
| `ConversationManager.cs:501,508,519` | Tool execution errors/exceptions |
| `ConversationManager.cs:550` | Exception during LLM message send |
| `ConversationManager.cs:686` | Approval flow exception |
| `ConversationManager.cs:775` | Bash execution exception |
| `ConversationManager.cs:866,953` | Write/edit exception |
| `ProviderManager.cs:75-121` | Provider config errors (5 distinct messages) |
| `ConfigurationService.cs:65` | System prompt load error |
| `FileContentExtractor.cs:23,54,89,114` | File/image/PDF errors |
| `McpManager.cs:184` | MCP server connection error |
| `FakeToolManager.cs:53` | Fake tools load warning |

### 7. Informational / Success — Confirming something happened
| Where | What |
|-------|------|
| `ConversationManager.cs:71` | Provider switched |
| `ConversationManager.cs:1170` | History cleared |
| `CommandProcessor.cs:134,149` | File/image inserted into context |
| `CommandProcessor.cs:236,244` | Skill loaded/unloaded |
| `ConfigurationService.cs:59` | Using default system prompt |
| `ConversationManager.cs:1027,1130` | History save/load warnings |

### 8. Lists & Help — Enumerating available things
| Where | What |
|-------|------|
| `Program.cs:134-147` | `--help` text (via `Console.WriteLine`) |
| `CommandProcessor.cs:254-264` | `?` help command listing |
| `CommandProcessor.cs:209-219` | `/skills` listing |
| `ProviderManager.cs:146-191` | Provider listing (two separate displays) |
| `PromptProcessor.cs:25-29` | MCP prompt listing |

### 9. Diagnostics — Debug/development output
| Where | What |
|-------|------|
| `ConversationManager.cs:1158-1160` | Tool diagnostic display (name, input, output) |
| `Program.cs:159` | `--dump-tools` path output to stderr |

## Inconsistencies

- **Mixed APIs for the same job**: Help text uses raw `Console.WriteLine`, command help uses `AnsiConsole.MarkupLine`. Some `Console.Write` usage is intentional — Spectre.Console doesn't handle double-byte characters correctly, and some output uses raw ANSI sequences generated by PxSharp. These are legitimate reasons to bypass Spectre, but the boundary between "intentional `Console.Write`" and "accidental `Console.Write`" isn't obvious from the code.
- **Approval UX reimplemented per tool type**: Bash, write, edit, and MCP tools each have their own inline approve/reject/preview flow with slightly different structure and key handling.
- **No consistent error format**: Some errors use `[red]Error:[/]`, some just use red text, some warnings use yellow. No standard prefix or severity indicator.
- **Tool status output varies**: Some show `→ tool: detail`, others use different arrow/prefix patterns.
- **Input method grab bag**: `Console.ReadKey()`, `Console.ReadLine()`, `AnsiConsole.Ask`, `AnsiConsole.Prompt`, and `AnsiConsole.Confirm` are all used for different input scenarios with no unifying pattern.
- **List rendering is ad-hoc**: Providers, skills, prompts, and help commands all format their lists differently.

## Possible Normalization Targets

1. **Centralized output helpers** — e.g. `UI.Error()`, `UI.Success()`, `UI.Status()`, `UI.Info()` that enforce consistent prefixes, colors, and formatting.
2. **Unified approval flow** — one method parameterized by tool type (what to show, what preview means) instead of 3-4 inline implementations.
3. **Consistent list rendering** — one pattern for "here are N things" (providers, skills, prompts, help items).
4. **Clarify the Console.Write boundary** — raw `Console.Write` is legitimate for double-byte characters (Spectre bug) and PxSharp ANSI output. Rather than eliminating it, make the intent explicit (e.g. route through a helper that documents *why* it bypasses Spectre).
