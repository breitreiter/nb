# Fake Tool Response Macros

**Status:** Proposed
**Problem:** Fake tool responses are static strings. When a model calls `CreateThing()` twice, it gets the same ID back both times, causing confusion and angry loops. Responses need just enough dynamism to keep the model happy without building a real backend.

## Macro Syntax

Use `{{macro}}` or `{{macro(args)}}` in the `response` field of `fake-tools.yaml`. Macros are expanded at invocation time, so each call produces a fresh result.

## Proposed Macros

### `{{$guid}}`
Random UUID. The bread-and-butter fix for the duplicate ID problem.

```yaml
response: '{"id": "{{$guid}}", "status": "created"}'
# → {"id": "a3f1b2c4-5678-9abc-def0-1234567890ab", "status": "created"}
```

### `{{$int}}` / `{{$int(min,max)}}`
Random integer. No args = 1-10000. With args = range.

```yaml
response: '{"id": {{$int}}, "count": {{$int(1,5)}}}'
# → {"id": 7382, "count": 3}
```

### `{{$param.name}}`
Echo back the value of a parameter the model supplied. Useful for confirmations ("created user X") so the model sees its input reflected.

```yaml
- name: create_user
  parameters:
    - name: username
      type: string
      required: true
    - name: role
      type: string
      required: false
  response: '{"id": "{{$guid}}", "username": "{{$param.username}}", "role": "{{$param.role}}"}'
# Model calls create_user(username="alice", role="admin")
# → {"id": "f47ac10b-...", "username": "alice", "role": "admin"}
```

Missing/undefined params resolve to empty string.

### `{{$timestamp}}`
ISO 8601 UTC timestamp. Models expect timestamps on created/modified resources.

```yaml
response: '{"id": "{{$guid}}", "created_at": "{{$timestamp}}"}'
# → {"id": "...", "created_at": "2026-02-25T14:30:00.000Z"}
```

### `{{$counter(name)}}`
Auto-incrementing integer, scoped by name, persisted for the session. Different counter names track independently. Starts at 1.

```yaml
- name: create_ticket
  response: '{"ticket_id": "TICK-{{$counter(tickets)}}", "status": "open"}'
# First call:  {"ticket_id": "TICK-1", "status": "open"}
# Second call: {"ticket_id": "TICK-2", "status": "open"}
```

### `{{$choice(a,b,c,...)}}`
Random pick from a comma-separated list. For varied responses that feel less robotic.

```yaml
response: '{"status": "{{$choice(pending,processing,queued)}}"}'
# → {"status": "processing"}
```

### `{{$random_string(length)}}`
Random alphanumeric string of given length. Default 8.

```yaml
response: '{"token": "{{$random_string(32)}}"}'
# → {"token": "aB3kF9mNpQ2xR7wY1cD6eH4jL8sT0vU"}
```

## Implementation

### Macro Processor
New class `MacroProcessor` (or just a static method in `FakeToolManager`) that takes a response template string + the parameter dictionary and returns the expanded string.

- Parse `{{...}}` tokens with a regex
- Match against known macro names
- Expand each, leave unrecognized macros as literal text (don't blow up)
- Counter state lives as a `Dictionary<string, int>` on `FakeToolManager` (session-scoped, resets on restart)

### Integration Point
In `ConversationManager.cs` where fake tool responses are returned (~line 280):

```csharp
// Before:
allToolResults.Add(new FunctionResultContent(functionCall.CallId, fakeTool.Response));

// After:
var expandedResponse = _fakeToolManager.ExpandMacros(fakeTool.Response, functionCall.Arguments);
allToolResults.Add(new FunctionResultContent(functionCall.CallId, expandedResponse));
```

The display/logging should also show the expanded response, not the template.

### Parsing Approach
Simple regex: `\{\{\$(\w+)(?:\(([^)]*)\))?\}\}` captures:
- Group 1: macro name (e.g., `guid`, `int`, `param.username`, `counter`)
- Group 2: optional args (e.g., `1,100`, `tickets`, `pending,processing,queued`)

For `$param.X`, split on `.` — group 1 is `param` and the rest is the parameter path.

### No New Dependencies
All macros use `System.Guid`, `System.Random`, `DateTime.UtcNow` — nothing external needed.

## What This Doesn't Do

- No conditionals or logic (`{{$if ...}}`) — this isn't a template engine
- No cross-tool state (one tool's output feeding another) — the model handles that naturally once IDs are unique
- No persistence across sessions — counters reset on restart, which is fine

## Example: The Original Problem, Solved

```yaml
fake_tools:
  - name: CreateThing
    description: "Create a new thing"
    parameters:
      - name: name
        type: string
        required: true
    response: '{"id": "{{$guid}}", "name": "{{$param.name}}", "created_at": "{{$timestamp}}"}'

  - name: LinkThing
    description: "Link two things together"
    parameters:
      - name: source_id
        type: string
        required: true
      - name: target_id
        type: string
        required: true
    response: '{"link_id": "{{$guid}}", "source": "{{$param.source_id}}", "target": "{{$param.target_id}}", "linked_at": "{{$timestamp}}"}'
```

Model calls `CreateThing("Widget A")` → gets `id: "a1b2c3..."`. Calls `CreateThing("Widget B")` → gets `id: "d4e5f6..."` (different!). Calls `LinkThing("a1b2c3...", "d4e5f6...")` → success with both IDs echoed back. No confusion.
