# Project Context for Claude

## Project Overview
NotaBene (nb) - A C# console application that provides both interactive and single-shot chat modes with Azure OpenAI integration, MCP (Model Context Protocol) support, and persistent conversation history.

## Key Technologies
- **Language**: C# (.NET)
- **UI Framework**: Spectre.Console for terminal UI
- **AI Integration**: Azure OpenAI
- **Architecture**: MCP-enabled chat application with dual execution modes (interactive/single-shot) and persistent conversation history

## Important Documentation Links
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

# Run the application
dotnet run

# Add any test commands here when available
```

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
- `ConversationManager.cs` - Handles LLM interactions, MCP tool integration, and conversation history serialization
- `McpManager.cs` - Manages MCP client connections
- `ConfigurationService.cs` - Configuration management
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
- **Multimodal Support**: Image insertion (JPG, PNG) with binary data handling for o4-mini vision capabilities

## MCP Implementation Details
- **McpManager.cs** - Manages MCP client lifecycle and exposes tools/prompts via interfaces
- **Tool Integration** - MCP tools are automatically converted to `ChatTool` format in `ConversationManager.cs:186-223`
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

## Coding Conventions
- Follow existing C# conventions in the codebase
- Use Spectre.Console markup for colored terminal output
- Handle exceptions gracefully with user-friendly error messages

## Development Best Practices
- When adding significant new features, or new configuration requirements, ask if you should update the readme.md
- Ask before adding an interface, unless there is an immediate, obvious reason to do so. Don't create new interfaces for "future flexibility."
- Avoid building DI scaffolding unless you're working with a library or package that expects you to use DI.

## Architecture Notes
- **Model Isolation**: LLM interactions are isolated to `ConversationManager.cs` for easy provider swapping
- **Refactored Structure**: Commands (`CommandProcessor`), file operations (`FileContentExtractor`), prompts (`PromptProcessor`) separated for maintainability
- **Safety Mechanisms**: Max tool calls per message, parameter validation, graceful error handling

## Important Workflow Reminders
- When changing the structure of appsettings.json make sure to update appsettings.example.json

## Debugging Strategies
- When resolving code compilation problems, before attempting to refactor, check to see if the project is missing a dependency