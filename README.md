# NotaBene (nb)

A command-line chat interface for Azure OpenAI with MCP (Model Context Protocol) server support and direct file content integration.

## Features

- Interactive and single-shot execution modes
- Direct file content insertion (PDF, TXT, MD)
- MCP server integration for extended tooling
- Directory-based conversation history
- Designed for Azure OpenAI o4-mini

## Prerequisites

- .NET 8.0 or later
- Azure OpenAI resource with:
  - Chat model deployment (You may need to tweak the context window size in ConversationManager.cs if you're not using o4-mini)

## Setup

1. Clone and configure:
   ```bash
   git clone https://github.com/breitreiter/nb
   cd nb
   cp appsettings.example.json appsettings.json
   ```

2. Edit `appsettings.json` with your Azure OpenAI credentials:
   ```json
   {
     "AzureOpenAI": {
       "Endpoint": "https://your-resource-name.openai.azure.com/",
       "ApiKey": "your-api-key-here",
       "ChatDeploymentName": "o4-mini"
     }
   }
   ```

3. Optionally create `system.md` for a custom system prompt:
   ```markdown
   You are a helpful AI assistant specialized in software development.
   ```

4. Optionally configure MCP servers by copying `mcp.example.json` to `mcp.json` and editing:
   ```json
   {
     "inputs": [],
     "servers": {
       "test-server": {
         "type": "stdio",
         "command": "npx",
         "args": ["-y", "@modelcontextprotocol/server-everything"]
       }
     }
   }
   ```

5. Build and run:
   ```bash
   dotnet build
   dotnet run
   ```

## Usage

### Interactive Mode
Launch with no parameters to start an interactive chat session:
```bash
nb
```
In interactive mode, you can use these commands:
- `exit` - Quit the application
- `/clear` - Clear conversation history (preserves system prompt)
- `/insert <filepath>` - Insert entire file content into conversation context (PDF or text)
- `/prompts` - List available MCP prompts from connected servers
- `/prompt <name>` - Invoke a specific MCP prompt with interactive argument collection
- `?` - Show help with all commands

### Single-Shot Mode
Launch with parameters to execute a single command and exit immediately:
```bash
# Send a chat message and exit
nb "What is the capital of France?"

# Execute a command and exit
nb "/clear"
nb "/insert document.pdf"
nb "/prompts"
nb "/prompt weather-report"
```

### Directory-Based Conversation History
Conversation history saves to `.nb_conversation_history.json` in the current working directory. Each directory maintains its own context:

```bash
# In your project directory - build up context
cd /path/to/my-project
nb "/insert README.md"
nb "/insert src/main.py"
nb "Analyze this codebase structure"

# Each directory maintains its own conversation
cd /path/to/different-project
nb  # Fresh conversation for this project

# Switch back to continue previous conversation
cd /path/to/my-project  
nb  # Loads previous conversation context
```

Single-shot mode maintains conversation continuity between invocations.

### File Content Insertion
Insert entire file contents directly into the conversation:
```bash
nb "/insert /path/to/document.pdf"
nb "/insert ./notes.md"  
nb "/insert data.txt"
```
This adds the complete file content to your message context.

### MCP Prompt Integration
List and invoke prompts from connected MCP servers:
```bash
/prompts                    # List available prompts
/prompt weather-report      # Invoke a specific prompt
```
Prompts may request arguments interactively before execution.

### Built-in MCP Server
The project includes a test server (`mcp-servers/mcp-tester/`) with basic tools and dynamically generated prompts from markdown files.

To use the built-in server, configure it in your `mcp.json`:
```json
{
  "servers": {
    "built-in-tester": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "mcp-servers/mcp-tester/mcp-tester.csproj"]
    }
  }
}
```

## Configuration

### appsettings.json
| Setting | Description | Default |
|---------|-------------|---------|
| `AzureOpenAI:Endpoint` | Your Azure OpenAI endpoint | Required |
| `AzureOpenAI:ApiKey` | Your Azure OpenAI API key | Required |
| `AzureOpenAI:ChatDeploymentName` | Chat model deployment name | `o4-mini` |

### system.md
Place a `system.md` file in the same directory as the executable to customize the AI's behavior. The entire file content will be used as the system prompt.

### mcp.json
Configure MCP servers to extend the AI with additional tools and capabilities. Each server runs as a separate process and communicates via stdio.

| Field | Description | Example |
|-------|-------------|---------|
| `servers.{name}.command` | Command to run the server | `"dotnet"`, `"npx"` |
| `servers.{name}.args` | Arguments for the command | `["run", "--project", "path/to/server.csproj"]` |
| `servers.{name}.type` | Transport type (currently only stdio) | `"stdio"` |

Examples:
- Built-in server: `"command": "dotnet", "args": ["run", "--project", "mcp-servers/mcp-tester/mcp-tester.csproj"]`
- Node.js server: `"command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"]`

## Building for Distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Include `system.md` and `mcp.json` with your executable for custom configurations.

## Technical Details

- .NET 8.0
- Azure OpenAI .NET SDK v2.2.0-beta.4
- Model Context Protocol (MCP) SDK
- iText7 for PDF extraction
- Spectre.Console for terminal UI
- Directory-based conversation persistence

## License

MIT License
