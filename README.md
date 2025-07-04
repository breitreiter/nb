# NotaBene (nb)

A simple command-line chat interface for Azure OpenAI with MCP (Model Context Protocol) server support.

## Features

- **Interactive Mode** - Run conversations in a chat loop, or pass command-line arguments as a single prompt
- **MCP Server Integration** - Connect to Model Context Protocol servers for extended tool functionality
- **GPT-4o Mini Required** - Designed for Azure OpenAI's o4-mini deployment, may not work correctly with other models

## Prerequisites

- .NET 8.0 or later
- Azure OpenAI resource with o4-mini deployment

## Setup

1. **Clone the repository**
   ```bash
   git clone <your-repo-url>
   cd nb
   ```

2. **Configure Azure OpenAI**
   ```bash
   cp appsettings.example.json appsettings.json
   ```
   Edit `appsettings.json` with your Azure OpenAI credentials:
   ```json
   {
     "AzureOpenAI": {
       "Endpoint": "https://your-resource-name.openai.azure.com/",
       "ApiKey": "your-api-key-here",
       "DeploymentName": "your-deployment-name"
     }
   }
   ```

3. **Create system prompt (optional)**
   Create a `system.md` file with your desired system prompt:
   ```markdown
   You are a helpful AI assistant specialized in software development.
   Always provide clear, concise answers with code examples when relevant.
   ```

4. **Configure MCP servers (optional)**
   ```bash
   cp mcp.example.json mcp.json
   ```
   Edit `mcp.json` to add your MCP servers:
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

5. **Build and run**
   ```bash
   dotnet build
   dotnet run
   ```

## Usage

### Interactive Mode
```bash
dotnet run
```
Start a conversation session. Type `exit` to quit.

### Direct Query
```bash
dotnet run "What is the capital of France?"
```
Send a single message and get a response.

## Configuration

### appsettings.json
| Setting | Description | Default |
|---------|-------------|---------|
| `AzureOpenAI:Endpoint` | Your Azure OpenAI endpoint | Required |
| `AzureOpenAI:ApiKey` | Your Azure OpenAI API key | Required |
| `AzureOpenAI:DeploymentName` | Model deployment name | `o4-mini` |

### system.md
Place a `system.md` file in the same directory as the executable to customize the AI's behavior. The entire file content will be used as the system prompt.

### mcp.json
Configure MCP servers to extend the AI with additional tools and capabilities. Each server runs as a separate process and communicates via stdio.

| Field | Description | Example |
|-------|-------------|---------|
| `servers.{name}.command` | Command to run the server | `"dotnet"`, `"npx"` |
| `servers.{name}.args` | Arguments for the command | `["run", "--project", "path/to/server.csproj"]` |
| `servers.{name}.type` | Transport type (currently only stdio) | `"stdio"` |

**Examples:**
- **.NET MCP Server**: `"command": "dotnet", "args": ["run", "--project", "MyServer.csproj"]`
- **Node.js MCP Server**: `"command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"]`

## Building for Distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Make sure to include `system.md` and `mcp.json` alongside your executable if you want custom system prompts and MCP server configurations.

## Technical Details

- Built with .NET 8.0
- Uses Azure OpenAI .NET SDK (v2.2.0-beta.4)
- Model Context Protocol (MCP) SDK for tool integration
- Powered by Spectre.Console for rich terminal UI
- Automatic UTF-8 console setup for Windows
- Conversation history maintained in memory
- Supports tool calling via MCP servers

## License

MIT License - feel free to use and modify as needed.