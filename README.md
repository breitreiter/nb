# NotaBene (nb)

A feature-rich AI CLI.

![NotaBene Preview](preview.png)

## Features

- **Multi-Provider AI Support**: Built-in support for Azure OpenAI, OpenAI, Anthropic Claude, and Google Gemini
- **Runtime Provider Switching**: Switch between AI providers mid-conversation without losing context
- **Interactive and Single-Shot Modes**: Use interactively or execute single commands for scripting
- **File Insertion** (PDF, TXT, MD, JPG, PNG) with multimodal support for vision-capable models
- **MCP Server Integration** (stdio) for extensible tools and prompts
- **Directory-Based Conversation History**: Each directory maintains its own persistent context
- **Pluggable Provider Architecture**: Easy extensibility for additional AI services

## Prerequisites

- .NET 8.0 or later
- API key for at least one supported AI provider:
  - Azure OpenAI
  - OpenAI
  - Anthropic Claude
  - Google Gemini

## Setup

1. Clone and configure:
   ```bash
   git clone https://github.com/breitreiter/nb
   cd nb
   cp appsettings.example.json appsettings.json
   ```

2. Edit `appsettings.json` with your AI provider configuration:
   ```json
   {
     "ActiveProvider": "AzureOpenAI",
     "ChatProviders": [
       {
         "Name": "AzureOpenAI",
         "Endpoint": "https://your-resource-name.openai.azure.com/",
         "ApiKey": "your-api-key-here",
         "ChatDeploymentName": "o4-mini"
       },
       {
         "Name": "Anthropic",
         "ApiKey": "your-anthropic-api-key-here",
         "Model": "claude-3-7-sonnet-20250219"
       },
       {
         "Name": "OpenAI",
         "ApiKey": "sk-your-openai-api-key-here",
         "Model": "gpt-4o-mini"
       },
       {
         "Name": "Gemini",
         "ApiKey": "your-gemini-api-key-here",
         "Model": "gemini-2.0-flash-exp"
       }
     ]
   }
   ```

   You can configure multiple providers and switch between them at runtime. Only the `ActiveProvider` needs valid credentials to start, but configuring all providers allows seamless switching.

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
- `/insert <filepath>` - Insert file content into conversation context (PDF, text, JPG, PNG)
- `/providers` - List all available AI providers and their configuration status
- `/provider <name>` - Switch to a different AI provider (e.g., `/provider Anthropic`)
- `/prompts` - List available MCP prompts from connected servers
- `/prompt <name>` - Invoke a specific MCP prompt with interactive argument collection
- `?` - Show help with all commands

### Single-Shot Mode
Launch with parameters to execute a single command and exit immediately:
```bash
# Send a chat message and exit
nb What is the capital of France?

# Execute a command and exit
nb /clear
nb /insert document.pdf
nb Summarize this document
```

Conversation history saves to `.nb_conversation_history.json` in the current working directory. Each directory maintains its own context, and single-shot mode maintains conversation continuity between invocations.

### Provider Switching
Switch between AI providers during a conversation to leverage different models' strengths:
```bash
/providers                 # List all available providers
/provider Anthropic        # Switch to Claude
/provider OpenAI           # Switch to GPT models
/provider Gemini           # Switch to Google Gemini
```

Conversation history is maintained when switching providers, allowing you to continue the same conversation with different AI models.

### MCP Prompts
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

### Fake Tools
nb will read fake-tools.yaml and treat those definitions as normal tools. When the model requests a fake tool, nb will return the static response configured in the yaml file. Fake tool definitions will override MCP definitions.

Use fake tools to validate/tune tool descriptions, structure, and responses.

## Configuration

### appsettings.json

#### Provider Configuration
| Setting | Description | Example |
|---------|-------------|---------|
| `ActiveProvider` | Which provider to use on startup | `"AzureOpenAI"` |
| `ChatProviders` | Array of provider configurations | See below |

#### Supported Providers

**Azure OpenAI**
```json
{
  "Name": "AzureOpenAI",
  "Endpoint": "https://your-resource-name.openai.azure.com/",
  "ApiKey": "your-api-key-here",
  "ChatDeploymentName": "o4-mini"
}
```

**OpenAI**
```json
{
  "Name": "OpenAI",
  "ApiKey": "sk-your-openai-api-key-here",
  "Model": "gpt-4o-mini"
}
```

**Anthropic Claude**
```json
{
  "Name": "Anthropic",
  "ApiKey": "your-anthropic-api-key-here",
  "Model": "claude-3-7-sonnet-20250219"
}
```

**Google Gemini**
```json
{
  "Name": "Gemini",
  "ApiKey": "your-gemini-api-key-here",
  "Model": "gemini-2.0-flash-exp"
}
```

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

## AI Provider Architecture

nb includes four built-in AI providers and supports extensibility for additional services:

### Built-in Providers
- **Azure OpenAI** - Microsoft's enterprise OpenAI service
- **OpenAI** - Direct OpenAI API integration
- **Anthropic** - Claude models with function calling support
- **Google Gemini** - Google's generative AI models

All providers are automatically compiled into the `bin/{Config}/net8.0/providers/` directory during build.

### Provider Extensibility

nb uses a pluggable provider architecture built on Microsoft.Extensions.AI. You can extend support to additional AI services by:

1. **Using existing providers**: The four built-in providers cover most use cases
2. **Building custom providers**: Implement the `IChatClientProvider` interface in a separate assembly

### Provider Directory Structure
```
Providers/
├── AzureOpenAI/
│   ├── nb.Providers.AzureOpenAIProvider.csproj
│   └── AzureOpenAIProvider.cs
├── OpenAI/
│   ├── nb.Providers.OpenAIProvider.csproj
│   └── OpenAIProvider.cs
├── Anthropic/
│   ├── nb.Providers.AnthropicProvider.csproj
│   └── AnthropicProvider.cs
└── Gemini/
    ├── nb.Providers.GeminiProvider.csproj
    └── GeminiProvider.cs
```

Each provider is isolated in its own directory with separate dependencies to avoid conflicts. Providers are automatically loaded at runtime using `AssemblyLoadContext`.

## Technical Details

- **.NET 8.0** - Modern C# runtime
- **Microsoft.Extensions.AI** - Unified abstraction for AI providers with cross-provider compatibility
- **Provider Support**:
  - Azure.AI.OpenAI for Azure OpenAI
  - Microsoft.Extensions.AI.OpenAI for OpenAI
  - Anthropic.SDK for Claude models
  - Mscc.GenerativeAI.Microsoft for Google Gemini
- **Model Context Protocol (MCP) SDK** - Extensible tool integration
- **iText7** - PDF extraction and processing
- **Spectre.Console** - Rich terminal UI
- **AssemblyLoadContext** - Isolated provider loading to prevent dependency conflicts
- **Directory-based conversation persistence** - JSON-serialized chat history per working directory
- **Runtime provider switching** - Seamless model switching with conversation continuity

## License

MIT License
