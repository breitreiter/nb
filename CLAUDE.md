# Project Context for Claude

## Project Overview
NotaBene (nb) - A C# console application that provides both interactive and single-shot chat modes with pluggable AI provider support, MCP (Model Context Protocol) integration, and persistent conversation history.

## Key Technologies
- **Language**: C# (.NET)
- **UI Framework**: Spectre.Console for terminal UI
- **AI Integration**: Microsoft.Extensions.AI with pluggable provider architecture
- **Architecture**: MCP-enabled chat application with dual execution modes (interactive/single-shot), persistent conversation history, and extensible AI provider system

## Important Documentation Links
- [Microsoft.Extensions.AI Documentation](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Azure OpenAI .NET SDK](https://github.com/openai/openai-dotnet)
- [Azure OpenAI SDK Examples](https://github.com/openai/openai-dotnet/tree/main/examples)
- [Spectre.Console Documentation](https://spectreconsole.net/)
- [MCP Specification](https://modelcontextprotocol.io/)
- [.NET MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [iText7 PDF Library](https://itextpdf.com/en/products/itext-7)

## Build & Test Commands
```bash
# Build the project
dotnet build

# Run from bin directory (providers only load from here)
cd bin/Debug/net8.0 && ./nb

# Single-shot mode for quick tests
cd bin/Debug/net8.0 && ./nb "your prompt here"
```

Note: `dotnet run` from project root won't work - provider DLLs are discovered relative to the executable.

## Execution Modes
The application supports two execution modes:

1. **Interactive Mode** (no command line arguments)
   - Starts continuous chat loop with directory context banner
   - Loads conversation history from `.nb_conversation_history.json` in current directory
   - Saves conversation history on exit
   - Supports all commands and features

2. **Single-Shot Mode** (with command line arguments)  
   - Executes single command/prompt and exits immediately
   - Maintains conversation history between single-shot executions
   - Perfect for scripting and batch operations
   - Each directory maintains separate conversation context

## Project Structure
- `Program.cs` - Main entry point, handles dual execution modes and history persistence
- `ConversationManager.cs` - Handles LLM interactions using Microsoft.Extensions.AI, MCP tool integration, and conversation history serialization
- `ProviderManager.cs` - Manages AI provider discovery and loading (plugin architecture)
- `IChatClientProvider.cs` - Interface for AI provider plugins
- `AzureOpenAIProvider.cs` - Built-in Azure OpenAI provider implementation
- `McpManager.cs` - Manages MCP client connections
- `ConfigurationService.cs` - Configuration management
- `Shell/` - Native bash tool implementation for shell command execution
- `providers/` - Directory for external AI provider plugins (DLLs)
- `mcp-servers/mcp-tester/` - Built-in MCP server for testing and example prompts

## Custom Commands
The application supports these built-in commands (intercepted before LLM):
- `exit` - Quit the application
- `/clear` - Clear conversation history (preserves system prompt)
- `/insert <filepath>` - Insert file content into conversation context (supports PDF, TXT, MD, images: JPG, PNG)
- `/prompts` - List available MCP prompts from connected servers
- `/prompt <name>` - Invoke a specific MCP prompt with interactive argument collection
- `?` - Show help with all commands

## Development Notes
- Commands are intercepted in `CommandProcessor.cs` and processed via enum-based actions
- New commands should follow the existing pattern using `CommandResult` return types
- Configuration is loaded from `appsettings.json`
- MCP clients are initialized on startup and disposed on exit
- Tool calling safety: Max 3 tool calls per message to prevent infinite loops
- **Directory-Based History**: Automatically persisted to `.nb_conversation_history.json` in current working directory
- **Project Context**: Each directory maintains its own conversation history, perfect for project-specific AI assistance
- **Multimodal Support**: Image insertion (JPG, PNG) with DataContent handling for vision-capable models
- **Provider Architecture**: Pluggable AI providers via IChatClientProvider interface, supporting any Microsoft.Extensions.AI compatible provider

## AI Provider Plugin Architecture
- **nb.Providers.Abstractions** - Lightweight interface library containing only `IChatClientProvider` interface
- **ProviderManager** - Discovers and loads providers from `providers/` directory using `AssemblyLoadContext` for proper isolation
- **Directory Isolation** - Each provider lives in its own subdirectory with separate `AssemblyLoadContext` to prevent version conflicts
- **Configuration** - Providers configured via `ActiveProvider` + `ChatProviders` array in appsettings.json (see Configuration Schema below)
- **No Built-in Providers** - All providers are external plugins (AzureOpenAI, Anthropic, OpenAI, Gemini, etc.) loaded at runtime
- **Runtime Discovery** - Providers are loaded at startup with graceful error handling for missing dependencies
- **Post-Build Deployment** - Provider projects auto-copy their output to `bin/{Config}/net8.0/providers/{name}/` via post-build events
- **⚠️ Assembly Context Gotcha** - Shared types across different `AssemblyLoadContext` instances can cause type mismatch issues. Keep provider interface communication simple and avoid passing complex objects between providers and main app beyond the `IChatClient` interface.
- **⚠️ CRITICAL: Provider Exclusions** - When adding new provider projects, you MUST add exclusions to `nb.csproj` to prevent the provider files from being included in the main project. Add three exclusion entries for each provider directory:
  ```xml
  <Compile Remove="Providers\{ProviderName}\**" />
  <EmbeddedResource Remove="Providers\{ProviderName}\**" />
  <None Remove="Providers\{ProviderName}\**" />
  ```
  Failure to add these exclusions will cause namespace conflicts, assembly resolution issues, and build errors.

## MCP Implementation Details
- **McpManager.cs** - Manages MCP client lifecycle and exposes tools/prompts via interfaces
- **Tool Integration** - MCP tools are automatically integrated with Microsoft.Extensions.AI tool system
- **Prompt Support** - MCP prompts are accessible via `/prompts` and `/prompt <name>` commands
- **Argument Collection** - Prompt arguments are collected interactively using `AnsiConsole.Ask<string>()`
- **Result Processing** - Prompt results are extracted from `TextContentBlock` messages and sent to LLM
- **Error Handling** - Graceful fallback when servers don't support prompts (some MCP servers are tools-only)
- **Configuration** - MCP servers configured in `mcp.json` with command, args, and environment variables
- **Transport** - Uses stdio transport (`StdioClientTransport`) for process-based MCP servers

## Built-in MCP Server
- **Location** - `mcp-servers/mcp-tester/` - Self-contained C# MCP server
- **Dynamic Prompts** - Automatically generates prompts from `.md` files in `Prompts/` directory
- **Parameter Support** - Supports up to 3 parameters using `{parameter}` syntax in markdown files
- **Tools** - Includes basic test tools (echo, reverse-echo, current-time)
- **Integration** - Added to solution file, builds alongside main project
- **Usage** - Configure in `mcp.json` with dotnet run command pointing to the project

## Bash Tool (Shell Integration)
Native tool that gives the model shell access with custom approval UX.

**Files:**
- `Shell/ShellEnvironment.cs` - Detects OS, shell, architecture, available tools at startup
- `Shell/BashTool.cs` - Executes commands with timeout, output truncation (sandwich strategy)
- `Shell/CommandClassifier.cs` - Classifies commands (read/write/delete/run) for approval display
- `Shell/ApprovalPatterns.cs` - Handles --approve flag patterns for automated testing

**Features:**
- Environment context injected into system prompt (OS, shell, available tools, cwd)
- Custom approval UX showing classified commands (e.g., "read: /etc/hosts" vs "run: yarn install")
- Dangerous command detection with warnings (sudo, rm -rf, etc.)
- Output sandwich truncation for large outputs (head + tail + stats)
- Pre-approval via `--approve` flag for scripting/testing

**Usage:**
```bash
# Interactive - prompts for approval
./nb "list the files here"

# Pre-approve commands for automation
./nb --approve "ls" --approve "cat *" "analyze the project structure"
```

**Two-directory model:**
- `launchDirectory` - where nb started, where history lives (immutable)
- `shellCwd` - where commands execute, can change via `set_cwd` tool

## Coding Conventions
- Follow existing C# conventions in the codebase
- Use Spectre.Console markup for colored terminal output
- Handle exceptions gracefully with user-friendly error messages

## Development Best Practices
- When adding significant new features, or new configuration requirements, ask if you should update the readme.md
- Ask before adding an interface, unless there is an immediate, obvious reason to do so. Don't create new interfaces for "future flexibility."
- Avoid building DI scaffolding unless you're working with a library or package that expects you to use DI.

## Feature Documents
- `Features/` contains design docs for planned and implemented features
- These capture intent and reasoning, not current behavior - don't update them to match code
- When implementing: update Status to "Implemented", add PR link
- When revising significantly: add a "Revisions" section, don't rewrite history

## Architecture Notes
- **Microsoft.Extensions.AI Integration**: Uses modern AI abstractions with IChatClient interface for provider independence
- **Provider Abstraction**: LLM interactions isolated through pluggable provider system for easy swapping between AI services
- **Refactored Structure**: Commands (`CommandProcessor`), file operations (`FileContentExtractor`), prompts (`PromptProcessor`) separated for maintainability
- **Safety Mechanisms**: Max tool calls per message, parameter validation, graceful error handling
- **Clean Type System**: Uses Microsoft.Extensions.AI types (ChatMessage, ChatOptions, ChatResponse) throughout

## Configuration Schema
The application uses an array-based provider configuration schema:
```json
{
  "ActiveProvider": "AzureOpenAI",
  "ChatProviders": [
    {
      "Name": "AzureOpenAI",
      "Endpoint": "https://...",
      "ApiKey": "...",
      "ChatDeploymentName": "gpt-4"
    },
    {
      "Name": "Anthropic",
      "ApiKey": "...",
      "Model": "claude-3-7-sonnet"
    }
  ]
}
```
- `ActiveProvider` - Selects which provider from the array to use
- `ChatProviders` - Array of provider configurations, each with a `Name` field matching the provider's implementation
- Provider-specific fields are read directly from the provider's config object (no nested paths)

## Important Workflow Reminders
- When changing the structure of appsettings.json make sure to update appsettings.example.json

## Debugging Strategies
- When resolving code compilation problems, before attempting to refactor, check to see if the project is missing a dependency
- Don't add nuget packages or attempt to alter/update the version of an installed package by modifying csproj files