# I/O Separation Architecture

Status: Proposal (Draft)

## Problem Statement

The current codebase has ~100 `Console.Read/Write` calls scattered across 9 files, with blocking input calls embedded directly in business logic. This creates two problems:

1. **Single-shot mode hangs** when a tool needs approval that wasn't pre-approved. It should fail immediately with a non-zero exit code.

2. **Terminal.Gui integration is blocked** because Terminal.Gui is event-driven. You can't call `Console.ReadKey()` in the middle of a tool execution loop—you need to show a dialog, return to the event loop, and handle the response via callback/async.

## Design Goals

1. **Single-shot mode**: Never block for input. Unapproved actions fail fast with informative errors and non-zero exit codes.

2. **Interactive mode**: Support event-driven UI. Approval requests become async operations that the UI layer resolves.

3. **Testability**: Core logic should be testable without mocking Console.

4. **Minimal disruption**: Don't rewrite everything. Draw a clean boundary and refactor incrementally.

## Proposed Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                        Program.cs                            │
│         (Entry point, mode detection, DI wiring)             │
└──────────────────────────┬──────────────────────────────────┘
                           │
           ┌───────────────┴───────────────┐
           ▼                               ▼
┌──────────────────────┐       ┌──────────────────────────────┐
│  SingleShotRunner    │       │     InteractiveRunner        │
│  ----------------    │       │     -----------------        │
│  • No blocking I/O   │       │  • Terminal.Gui Application  │
│  • Fail on unapproved│       │  • Event-driven approvals    │
│  • Exit codes matter │       │  • Two-pane layout           │
│  • Stdout is output  │       │  • Dialogs for approvals     │
└──────────┬───────────┘       └──────────────┬───────────────┘
           │                                  │
           │    ┌─────────────────────┐       │
           │    │  IApprovalProvider  │       │
           │    │  IOutputSink        │       │
           └───►│  (interfaces)       │◄──────┘
                └──────────┬──────────┘
                           │
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                   ConversationEngine                         │
│                   ------------------                         │
│  • Pure business logic, no Console calls                     │
│  • Injected dependencies for I/O                             │
│  • Async approval flow (not blocking loops)                  │
│  • Returns results, doesn't print them                       │
└─────────────────────────────────────────────────────────────┘
```

## Key Abstractions

### IApprovalProvider

Handles all user approval requests. Different implementations for different modes.

```csharp
public interface IApprovalProvider
{
    Task<ApprovalResult> RequestToolApprovalAsync(ToolApprovalRequest request);
    Task<ApprovalResult> RequestBashApprovalAsync(BashApprovalRequest request);
    Task<ApprovalResult> RequestWriteApprovalAsync(WriteApprovalRequest request);
}

public record ApprovalResult(
    bool Approved,
    string? RejectionReason = null
);

public record BashApprovalRequest(
    string Command,
    string? Description,
    ClassifiedCommand Classification,
    bool IsDangerous
);

// Similar records for ToolApprovalRequest, WriteApprovalRequest
```

### IOutputSink

Where output goes. Keeps business logic ignorant of presentation.

```csharp
public interface IOutputSink
{
    void WriteStatus(string message);
    void WriteError(string message);
    void WriteWarning(string message);
    void WriteToolCall(string toolName, string? arguments = null);
    void WriteToolResult(string toolName, int? exitCode = null);
    void WriteAssistantResponse(string content);
}
```

### Mode-Specific Implementations

**Single-shot mode:**

```csharp
class SingleShotApprovalProvider : IApprovalProvider
{
    private readonly ApprovalPatterns _preApproved;

    public Task<ApprovalResult> RequestBashApprovalAsync(BashApprovalRequest req)
    {
        if (_preApproved.IsApproved(req.Command))
            return Task.FromResult(new ApprovalResult(true));

        // Fail fast - never block, never prompt
        return Task.FromResult(new ApprovalResult(
            false,
            $"Command not pre-approved. Use --approve \"{req.Command}\" to allow."
        ));
    }
}

class SingleShotOutputSink : IOutputSink
{
    // Just writes to Console.WriteLine / Console.Error.WriteLine
    // Could also respect a --quiet flag
}
```

**Interactive mode:**

```csharp
class InteractiveApprovalProvider : IApprovalProvider
{
    private readonly Func<BashApprovalRequest, Task<ApprovalResult>> _bashDialogHandler;

    public Task<ApprovalResult> RequestBashApprovalAsync(BashApprovalRequest req)
    {
        // Delegate to UI layer (Terminal.Gui dialog)
        return _bashDialogHandler(req);
    }
}

class TerminalGuiOutputSink : IOutputSink
{
    private readonly TextView _outputView;

    public void WriteAssistantResponse(string content)
    {
        // Append to Terminal.Gui TextView, handle scrolling, etc.
    }
}
```

## Refactoring Strategy

### Phase 1: Define Interfaces and Request Types

Create the interfaces and data types without changing existing code. This is safe and lets us iterate on the API design.

**Files to create:**
- `Core/IApprovalProvider.cs`
- `Core/IOutputSink.cs`
- `Core/ApprovalRequests.cs` (record types)

### Phase 2: Extract ConversationEngine

The current `ConversationManager` does too much. Split it:

- **ConversationEngine**: Pure logic—manages history, calls LLM, processes tool calls. No I/O.
- **ConversationManager**: Thin orchestration layer that wires engine to I/O providers.

The engine's tool execution loop changes from:

```csharp
// BEFORE: Blocking approval in business logic
while (true)
{
    Console.Write("Execute? [Y/n/?] ");
    var key = Console.ReadKey().KeyChar;
    if (key == 'y') { /* execute */ break; }
    if (key == 'n') { /* reject */ break; }
}
```

To:

```csharp
// AFTER: Async approval, no Console calls
var approval = await _approvalProvider.RequestBashApprovalAsync(request);
if (!approval.Approved)
{
    return new ToolResult(rejected: true, reason: approval.RejectionReason);
}
// execute
```

### Phase 3: Implement Single-Shot Provider

Create `SingleShotApprovalProvider` with fail-fast behavior. Wire it up in `Program.cs` when args are present.

**Test case:** Run `nb "run ls" ` without `--approve "ls"`. Should exit non-zero with clear error, not hang.

### Phase 4: Implement Console Interactive Provider

Before Terminal.Gui, create a `ConsoleApprovalProvider` that does the current blocking behavior. This proves the abstraction works without introducing Terminal.Gui yet.

### Phase 5: Terminal.Gui Integration

Now Terminal.Gui can be added. `InteractiveRunner` creates a Terminal.Gui `Application`, wires up `TerminalGuiApprovalProvider` (shows dialogs), and `TerminalGuiOutputSink` (writes to views).

## File-by-File Impact Assessment

| File | Console Calls | Refactoring Needed |
|------|---------------|-------------------|
| `ConversationManager.cs` | ~50 | Heavy - extract engine, inject providers |
| `Program.cs` | ~15 | Medium - mode detection, wiring |
| `CommandProcessor.cs` | ~12 | Light - just output, use IOutputSink |
| `ProviderManager.cs` | ~15 | Light - just output, use IOutputSink |
| `PromptProcessor.cs` | ~8 | Medium - has blocking input for params |
| `ConfigurationService.cs` | ~3 | Light - just warnings |
| `FileContentExtractor.cs` | ~4 | Light - just errors |
| `McpManager.cs` | ~1 | Trivial |
| `FakeToolManager.cs` | ~1 | Trivial |

## Exit Code Strategy for Single-Shot Mode

| Situation | Exit Code | Meaning |
|-----------|-----------|---------|
| Success | 0 | Completed normally |
| Tool approval denied | 10 | Unapproved tool call attempted |
| Bash approval denied | 11 | Unapproved bash command attempted |
| Write approval denied | 12 | Unapproved file write attempted |
| LLM error | 20 | API call failed |
| Configuration error | 30 | Missing config, bad provider, etc. |

## Open Questions

1. **Should IOutputSink be sync or async?** Terminal.Gui might need async for thread marshaling. But sync is simpler for Console.

2. **Where does the "thinking" indicator live?** It's not really output—it's UI state. Maybe a separate `IProgressIndicator` interface?

3. **MCP prompt parameter collection** - Currently uses `Console.ReadLine()` in a loop. In single-shot mode, should these come from command-line args? Or should MCP prompts be interactive-only?

4. **How chatty should single-shot mode be?** Current code prints status messages. Should `--quiet` suppress everything except the final response?

5. **Should we preserve the current Console interactive mode?** Or go straight to Terminal.Gui? A Console fallback might be useful for SSH sessions without truecolor support.

## Estimated Effort

| Phase | Effort | Risk |
|-------|--------|------|
| Phase 1 (Interfaces) | 2-3 hours | Low |
| Phase 2 (Extract Engine) | 1-2 days | Medium - largest refactor |
| Phase 3 (Single-Shot Provider) | 2-3 hours | Low |
| Phase 4 (Console Interactive) | 3-4 hours | Low |
| Phase 5 (Terminal.Gui) | 1-2 days | Medium - new framework |

**Total: 3-5 days** depending on how clean we want the boundaries.

## Appendix: Current Approval Loops

For reference, here are the current blocking approval patterns that need to change:

**Bash command approval** (`ConversationManager.cs:478-510`):
```csharp
while (true)
{
    Console.Write($"Execute? {options} ");
    var key = Console.ReadKey().KeyChar;
    Console.WriteLine();

    if (key == 'n' || ...) { /* reject */ }
    else if (key == '?') { /* show details, continue loop */ }
    else if (key == 'y' || ...) { /* approve */ }
}
```

**MCP tool approval** (`ConversationManager.cs:293-314`):
```csharp
while (true)
{
    Console.WriteLine($"Allow tool call: {functionCall.Name}? (Y/n/?)");
    var key = Console.ReadKey().KeyChar;

    if (key == 'n') { approved = false; break; }
    else if (key == '?') { /* show args, then Confirm dialog */ }
    else if (key == '\r' || key == 'y') { approved = true; break; }
}
```

**Write file approval** (`ConversationManager.cs:591-635`):
```csharp
while (true)
{
    Console.Write("Execute? [y/N/?] ");
    var key = Console.ReadKey().KeyChar;

    if (key == 'n' || key == '\r') { /* reject */ }
    else if (key == '?') { /* show preview, continue */ }
    else if (key == 'y') { /* approve */ }
}
```

All three follow the same pattern and will become `await _approvalProvider.RequestXxxApprovalAsync(request)`.
