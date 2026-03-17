# Dependency Upgrade: M.E.AI 10.x + MCP SDK 1.x + Official Anthropic SDK

Status: Proposed

---

## Motivation

Several planned features (streaming, thinking tokens, MCP OAuth) are blocked by outdated dependencies. Rather than upgrade incrementally and fight compatibility issues at each step, this combines three dependency upgrades into one surgery:

1. **Microsoft.Extensions.AI 9.9.1 → 10.x** — Required by both the new MCP SDK and official Anthropic SDK. Touches every provider.
2. **ModelContextProtocol 0.4.0-preview.2 → 1.x** — Prerequisite for MCP OAuth (see `Features/MCP_OAuth.md`). Significant API churn between 0.4 and 1.0.
3. **Anthropic.SDK 5.5.3 → Official `Anthropic` 12.x** — Switch from community SDK to Anthropic's first-party SDK. Unlocks native thinking token support, faster feature parity.

Doing these together avoids two periods of instability and prevents the intermediate state where M.E.AI 10.x is running against a MCP SDK that doesn't expect it.

## Current State

| Package | Current | Target | Location |
|---|---|---|---|
| `Microsoft.Extensions.AI` | 9.9.1 | 10.x | All providers, abstractions |
| `Microsoft.Extensions.AI.Abstractions` | 9.9.1 | 10.x | `nb.Providers.Abstractions` |
| `Microsoft.Extensions.AI.OpenAI` | 9.9.1-preview | 10.x | OpenAI, AzureOpenAI providers |
| `ModelContextProtocol` | 0.4.0-preview.2 | 1.x | `nb.csproj`, `mcp-tester.csproj` |
| `Anthropic.SDK` | 5.5.3 | Remove | `AnthropicProvider.csproj` |
| `Anthropic` (official) | — | 12.x | `AnthropicProvider.csproj` (new) |
| `Microsoft.Extensions.Configuration` | 10.0.0-rc.1 | 10.0.0 (GA) | `nb.csproj` |
| `Microsoft.Extensions.Configuration.Binder` | 10.0.0-rc.1 | 10.0.0 (GA) | `nb.csproj` |
| `Microsoft.Extensions.Hosting` | 10.0.0-rc.2 | 10.0.0 (GA) | `mcp-tester.csproj` |

## What Unlocks After This

- **Streaming output** (`Features/Streaming_Output.md`) — clean path with all providers on M.E.AI 10.x
- **Thinking tokens** — official Anthropic SDK has first-class extended thinking / adaptive thinking support
- **MCP OAuth** (`Features/MCP_OAuth.md`) — MCP SDK 1.x has `ClientOAuthOptions` built in
- **Newer MCP protocol features** — resources, sampling, roots, whatever else landed between 0.4 and 1.0
- **Stabilized dependency graph** — no more preview/RC packages, everything on GA releases

## Risk Assessment

**High risk, bounded scope.** The codebase is small enough that every call site can be audited by hand. The provider plugin boundary limits blast radius — each provider can be updated and tested independently.

Biggest risks:
- **M.E.AI 10.x breaking changes** — `IChatClient` interface may have changed. Need to check if `GetResponseAsync`/`ChatResponse`/`ChatMessage` signatures shifted.
- **MCP SDK 1.x API churn** — Known to have significant changes. Every method call in `McpManager.cs` needs review.
- **AssemblyLoadContext version conflicts** — Providers load in isolated contexts. If M.E.AI abstractions version must match exactly between host and plugin, this could get tricky. Test this early.
- **Community provider SDKs** — Gemini (`Mscc.GenerativeAI.Microsoft`) and OpenAI (`OpenAI` 2.5.0) providers depend on their own M.E.AI adapters. Need to verify compatible versions exist.

## Implementation Plan

### Phase 1: M.E.AI 10.x Bump (Foundation)

Update M.E.AI across the entire solution. This is the riskiest and most cross-cutting change.

**Steps:**
1. Bump `Microsoft.Extensions.AI.Abstractions` to 10.x in `nb.Providers.Abstractions`
2. Bump `Microsoft.Extensions.AI` to 10.x in all provider projects
3. Bump `Microsoft.Extensions.AI.OpenAI` to 10.x in OpenAI and AzureOpenAI providers
4. Fix any breaking changes in `IChatClientProvider`, `ConversationManager`, and provider implementations
5. Bump `Microsoft.Extensions.Configuration*` and `Hosting` from RC to GA while we're in here
6. Build. Fix. Repeat.

**Key thing to check:** Did M.E.AI 10.x rename types, change method signatures, or alter the streaming API? The jump from 9.x to 10.x was a major version bump.

### Phase 2: MCP SDK 1.x

Upgrade MCP SDK and fix all call sites.

**Steps:**
1. Bump `ModelContextProtocol` to 1.x in `nb.csproj` and `mcp-tester.csproj`
2. Audit and fix all call sites in `McpManager.cs` — method signatures changed (new `RequestOptions` pattern, etc.)
3. Update `mcp-tester` server code if the server-side API changed
4. Verify: stdio transport still works, tools still register, prompts still load

**Reference:** The MCP OAuth doc (`Features/MCP_OAuth.md`) has notes on known breaking changes.

### Phase 3: Official Anthropic SDK

Replace community `Anthropic.SDK` with official `Anthropic` package.

**Steps:**
1. Remove `Anthropic.SDK` from `AnthropicProvider.csproj`
2. Add `Anthropic` (official) package
3. Rewrite `AnthropicProvider.cs` — the `IChatClient` creation path changes:
   - Old: `new AnthropicClient(apiKey).Messages` → `IChatClient`
   - New: `new AnthropicClient(apiKey).AsIChatClient(model)` → `IChatClient`
4. Verify tool calling still works through the M.E.AI abstraction
5. Verify model selection and configuration still works

This should be the smallest phase — the provider is a thin wrapper and the `IChatClient` contract is the same.

### Phase 4: Validate Everything

- Build the full solution (main app + all providers + mcp-tester)
- Test each provider: AzureOpenAI, OpenAI, Anthropic, Gemini, Mock
- Test MCP: stdio server connection, tool listing, tool execution, prompts
- Test conversation history: load old history file, verify it still deserializes
- Test single-shot and interactive modes
- Test trust mode and approval flows

### Phase 5: Opportunistic Cleanup

Only if things go smoothly:
- Remove any compatibility shims that are no longer needed
- Update model constants if the new SDKs ship newer defaults
- Update `appsettings.example.json` if configuration shape changed

## Conversation History Compatibility

**Risk:** `ChatMessage` serialization may differ between M.E.AI 9.x and 10.x. If the JSON shape changed, existing `.nb_conversation_history.json` files won't load.

**Mitigation:** Test deserialization of a 9.x-era history file with 10.x types. If it breaks, add a one-time migration or just handle the deserialization error gracefully (warn and start fresh).

## Provider-Specific Notes

### OpenAI / AzureOpenAI
- Depend on `Microsoft.Extensions.AI.OpenAI` which bridges the `OpenAI` NuGet package to `IChatClient`
- Need to verify a 10.x-compatible version of this bridge exists
- May also need to bump `OpenAI` and `Azure.AI.OpenAI` packages

### Gemini
- Uses `Mscc.GenerativeAI.Microsoft` (community package) for M.E.AI integration
- Need to verify it supports M.E.AI 10.x, or find an alternative
- Lowest priority — if this provider lags behind, it can be temporarily disabled

### Mock
- Trivial — just bump M.E.AI version, fix any interface changes

## Order of Operations (Summary)

```
1. Branch from master
2. M.E.AI 10.x across all projects        ← most breakage here
3. Fix compilation errors, provider by provider
4. MCP SDK 1.x in nb.csproj + mcp-tester  ← second most breakage
5. Fix McpManager.cs call sites
6. Swap Anthropic.SDK → official Anthropic  ← cleanest change
7. Rewrite AnthropicProvider.cs
8. Build, test everything
9. Single PR back to master
```

## References

- [Microsoft.Extensions.AI 10.x changelog](https://www.nuget.org/packages/Microsoft.Extensions.AI/)
- [MCP C# SDK releases](https://github.com/modelcontextprotocol/csharp-sdk/releases)
- [Official Anthropic .NET SDK](https://github.com/anthropics/anthropic-sdk-csharp)
- [MCP OAuth feature doc](Features/MCP_OAuth.md)
- [Streaming feature doc](Features/Streaming_Output.md)
