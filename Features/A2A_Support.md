# A2A (Agent-to-Agent) Protocol Support

Status: Proposal

## Overview

Add support for the Google A2A (Agent-to-Agent) protocol, enabling nb to discover and interact with remote AI agents as peers. Where MCP connects nb to tools and data sources, A2A would connect nb to other autonomous agents that can perform complex, long-running tasks independently.

## What A2A Is

A2A is an open protocol (now under the Linux Foundation, Apache 2.0) for communication between opaque AI agent systems. Current version is v0.3 (July 2025), not yet 1.0.

Core concepts:
- **Agent Card** - Discovery document at `/.well-known/agent.json` declaring an agent's skills, capabilities, security, and transport preferences
- **Task** - Unit of work with a lifecycle: `working` → `input_required` → `completed`/`failed`/`canceled`
- **Message** - Communication between agents, containing Parts (text, files, structured data)
- **Artifact** - Output/deliverable of a completed task
- **Context** - Groups related tasks into a conversation thread

Transport: JSON-RPC 2.0 over HTTP(S), with SSE streaming and webhook push notifications. gRPC binding also available.

## Why Consider A2A

MCP and A2A are complementary, not competing:

| | MCP | A2A |
|---|---|---|
| Purpose | Connect to tools/data | Connect to agents |
| Model | Structured function calls | Multi-turn conversations |
| Visibility | Host sees everything | Agents are opaque |
| State | Stateless invocation | Stateful task lifecycle |
| Duration | Synchronous return | Long-running, async |
| Transport | stdio/HTTP (local-first) | HTTP/gRPC (network-first) |

What A2A enables that MCP cannot:
- **Agent delegation** - Ask a specialized agent to handle a subtask (e.g., "code review agent, review this PR")
- **Long-running async work** - Start a task, disconnect, get notified when done
- **Human-in-the-loop** - Remote agent can pause and request more info via `input_required` state
- **Multi-turn collaboration** - Stateful conversations with context continuity across interactions

## Scope: Client Only

nb would act as an **A2A client** — discovering remote agents and delegating tasks to them. Exposing nb itself as an A2A server (so other agents can call nb) would require embedding ASP.NET Core hosting into a console app, which is a much larger change with questionable value for nb's use case.

### User Experience

Configuration in `a2a.json` (or a section in `appsettings.json`):
```json
{
  "A2AAgents": [
    {
      "Name": "code-reviewer",
      "Endpoint": "https://review-agent.example.com",
      "Auth": {
        "Type": "ApiKey",
        "Key": "..."
      }
    }
  ]
}
```

New commands:
- `/agents` - List configured remote agents and their skills (resolved from Agent Cards)
- `/agent <name> <message>` - Send a message to a specific agent, creating a task

In-conversation, the LLM could also be given A2A agents as "tools" that it can invoke, similar to how MCP tools work today. The LLM would see agent skill descriptions and decide when to delegate.

### Task Lifecycle UX

```
> /agent code-reviewer Review the changes in src/ConversationManager.cs

Connecting to code-reviewer...
✓ Agent: CodeReview Pro v2.1 (3 skills)
  Task created: task-abc123

◐ Agent is working...
  [status: working] Analyzing file structure...
  [status: working] Reviewing code patterns...

Agent needs input:
  "I see two versions of SendMessageInternalAsync. Should I review
   both or just the current implementation?"
> Just the current one

◐ Agent is working...
  [status: working] Completing review...

✓ Task completed
  [artifact] Code review with 3 findings (text/markdown)

## Code Review: ConversationManager.cs
...
```

## Implementation Sketch

### Dependencies

- `A2A` NuGet package (preview) — core protocol, client, models
- No need for `A2A.AspNetCore` (client-only)

### New Components

- **A2AManager** - Parallel to McpManager. Resolves Agent Cards at startup, maintains client connections, exposes agents as tools
- **A2AConfig** - Configuration model for agent endpoints and auth
- **Agent Card caching** - Cache resolved cards to avoid discovery on every startup

### Integration Points

1. **Tool registration** - A2A agents registered as `AIFunction` tools in `ConversationManager`, just like MCP tools. The LLM sees agent skills and can invoke them
2. **Task tracking** - New state to track in-flight A2A tasks, handle `input_required` by prompting the user
3. **Streaming** - SSE streaming from A2A maps to incremental output display (aligns with the Streaming Output feature proposal)
4. **Commands** - New `/agents` and `/agent` commands in CommandProcessor

### Estimated Components

| Component | Effort | Notes |
|-----------|--------|-------|
| A2AManager + Agent Card resolution | Medium | Card fetching, caching, auth setup |
| A2A client integration | Medium | Send messages, track tasks, handle streaming |
| Tool registration for LLM | Medium | Map agent skills to AIFunction tools |
| Task lifecycle UX | Medium | Status display, input_required handling, artifacts |
| Configuration schema | Low | JSON config, auth types |
| Commands (/agents, /agent) | Low | Similar to existing /prompts pattern |

## Risk Analysis

This is where the honest assessment lives. A2A support is technically interesting but carries significant risks for a project like nb.

### Risk 1: Protocol Immaturity (High)

A2A is at v0.3. Not 1.0. The .NET SDK is in preview with explicit warnings that APIs may change. Investing significant development time in a pre-1.0 protocol means:
- Breaking changes will require rework
- Edge cases in the spec will be discovered through pain
- The .NET SDK may lag behind spec changes (it's not the primary SDK — Python is)
- Auth/security in the .NET SDK is flagged by the community as underdeveloped

**Mitigation:** Wait for v1.0, or build behind a feature flag with minimal coupling to core code.

### Risk 2: No Ecosystem to Connect To (High)

The biggest practical problem: who is nb going to talk to? A2A is a protocol for agent-to-agent communication, but the ecosystem of publicly available A2A agents is essentially nonexistent for individual developers. The announced partnerships (Salesforce, SAP, ServiceNow) are enterprise platforms talking to each other, not services that a CLI chat tool would interact with.

Building A2A client support without A2A agents to connect to means:
- The feature ships but provides no immediate value
- Testing requires building your own A2A server, adding scope
- Users can't use the feature until the ecosystem matures

**Mitigation:** Only invest if you plan to also build or connect to specific A2A agents (e.g., a code review agent, a deployment agent). Otherwise this is building a dock with no boats.

### Risk 3: Complexity vs. Value (Medium-High)

nb's strength is being a focused, well-crafted CLI chat tool. A2A adds:
- A new connection type with different semantics than MCP (stateful tasks vs. stateless tools)
- Long-running async state management (tasks that outlive a single request-response cycle)
- A human-in-the-loop interaction pattern that complicates the conversation flow
- Authentication flows (OAuth, API keys) for remote services
- Error handling for network failures, timeouts, and agent unavailability

All of this for a capability that MCP + bash tools may already approximate. You can achieve "delegate to another agent" today by running another CLI tool via bash, or by connecting to an MCP server that wraps an agent. It's less elegant but it works now, with zero new code.

**Question to answer:** Is there a concrete workflow you want that requires A2A, or is this about future-proofing?

### Risk 4: Overlap with MCP (Medium)

MCP is also evolving. The MCP spec already supports HTTP transport, and there's discussion about adding richer interaction patterns. If MCP grows to cover some of A2A's use cases (which is plausible given the competitive pressure), nb could end up with two overlapping protocols to maintain.

The "use MCP for tools, A2A for agents" separation is clean in theory, but in practice the line between "tool" and "agent" is blurry. An MCP server that wraps an LLM and makes multi-step decisions is functionally an agent, even if the protocol treats it as a tool.

**Mitigation:** Monitor MCP's evolution. If MCP adds task lifecycle and async patterns, A2A may become redundant for nb's use cases.

### Risk 5: Maintenance Burden (Medium)

A second protocol integration means:
- Two sets of configuration to document and support
- Two connection types to debug when things go wrong
- Two sets of version compatibility to track
- The A2A NuGet package is pre-release, so dependency updates may require code changes

For a project maintained by a small team (or solo), each new subsystem is a tax on every future change.

### Risk Summary

| Risk | Severity | Likelihood | Impact |
|------|----------|------------|--------|
| Protocol breaks before 1.0 | High | Very likely | Rework |
| No agents to connect to | High | Certain (today) | Zero user value |
| Complexity exceeds value | Medium-High | Likely | Maintenance drag |
| MCP evolves to cover A2A's niche | Medium | Possible | Wasted effort |
| Ongoing maintenance burden | Medium | Certain | Reduced velocity on other features |

## Recommendation

**Wait.** Revisit when:

1. A2A reaches v1.0 and the .NET SDK is stable
2. There are publicly available A2A agents that nb users would actually want to connect to
3. A concrete workflow emerges that MCP + bash tools genuinely cannot support

If you want to stay ahead of the curve without full commitment, the cheapest option is to build a thin A2AManager that resolves Agent Cards and displays agent info (`/agents` command), without the full task lifecycle. This lets you validate the NuGet package, understand the protocol, and be ready to expand when the ecosystem catches up — at a fraction of the implementation cost.

## References

- [A2A Protocol Specification](https://a2a-protocol.org/latest/specification/)
- [A2A .NET SDK](https://github.com/a2aproject/a2a-dotnet)
- [A2A vs MCP (Official)](https://a2a-protocol.org/latest/topics/a2a-and-mcp/)
- [Microsoft Blog: A2A .NET SDK](https://devblogs.microsoft.com/foundry/building-ai-agents-a2a-dotnet-sdk/)
- [A2A Protocol GitHub](https://github.com/a2aproject/A2A)
