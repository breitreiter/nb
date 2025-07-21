# NotaBene (nb)

A command-line chat interface for Azure OpenAI with MCP (Model Context Protocol) server support and Retrieval-Augmented Generation (RAG) capabilities.

## Features

- **Interactive Mode** - Run conversations in a chat loop, or pass command-line arguments as a single prompt
- **Document Upload & RAG** - Upload PDF, TXT, and MD files for semantic search and context-aware responses
- **MCP Server Integration** - Connect to Model Context Protocol servers for extended tool functionality
- **Built-in Commands** - Directory navigation, file uploads, and help commands
- **o4-mini Required** - Designed for Azure OpenAI's o4-mini deployment, may not work correctly with other models

## Prerequisites

- .NET 8.0 or later
- Azure OpenAI resource with:
  - Chat model deployment (e.g., o4-mini)
  - Text embedding model deployment (e.g., text-embedding-3-small)

## Setup

1. **Clone the repository**
   ```bash
   git clone https://github.com/breitreiter/nb
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
       "ChatDeploymentName": "o4-mini",
       "EmbeddingDeploymentName": "text-embedding-3-small"
     },
     "SemanticMemory": {
       "ChunkSize": 256,          // Words per document chunk (smaller = more precise search)
       "ChunkOverlap": 64,        // Overlapping words between chunks (prevents context loss)
       "SimilarityThreshold": 0.5 // Min similarity score for search results (0.3-0.8 typical range)
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
Start a conversation session with the following commands:
- `exit` - Quit the application
- `/pwd` - Show current working directory
- `/cd <path>` - Change directory
- `/index <filepath>` - Upload and process file for semantic search (PDF or text)
- `/insert <filepath>` - Insert entire file content into conversation context (PDF or text)
- `/prompts` - List available MCP prompts from connected servers
- `/prompt <name>` - Invoke a specific MCP prompt with interactive argument collection
- `?` - Show help with all commands

### Direct Query
```bash
dotnet run "What is the capital of France?"
```
Send a single message and get a response.

### Document Upload & RAG
Upload documents to enhance conversations with relevant context:
```bash
/index /path/to/document.pdf
/index ./notes.md
/index research.txt
```
Once uploaded, the AI will automatically search through your documents when answering questions, providing context-aware responses based on your uploaded content.

### Direct File Content Insertion
Insert entire file contents directly into the conversation:
```bash
/insert /path/to/document.pdf
/insert ./notes.md  
/insert data.txt
```
This adds the complete file content to your message context, useful for detailed analysis of specific documents.

**Tuning RAG Performance:**
- Lower `ChunkSize` (e.g., 128) for more precise search on focused content
- Increase `ChunkOverlap` to preserve more context between chunks  
- Adjust `SimilarityThreshold` - lower values (0.3-0.4) include more results, higher values (0.6-0.8) are more selective

### MCP Prompt Integration
Interact with prompts from connected MCP servers:
```bash
/prompts                    # List all available prompts
/prompt weather-report      # Invoke a specific prompt
/prompt code-review         # Prompts may request arguments interactively
```
MCP prompts provide pre-configured workflows and can accept arguments to customize their behavior. The application will prompt you for any required arguments before executing the prompt.

### Built-in MCP Server
The project includes a built-in MCP test server (`mcp-servers/mcp-tester/`) that provides:

**Tools:**
- `echo` - Echoes messages back to the client
- `reverse-echo` - Returns messages in reverse
- `current-time` - Returns the current date and time

**Dynamic Prompts:**
- Automatically generates prompts from markdown files in `mcp-servers/mcp-tester/Prompts/`
- Supports parameterized prompts using `{parameter}` syntax
- Includes sample prompts: `favecolor`, `codereview`, `meetingnotes`

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
| `AzureOpenAI:EmbeddingDeploymentName` | Embedding model deployment name | `text-embedding-3-small` |
| `SemanticMemory:ChunkSize` | Words per document chunk | `256` |
| `SemanticMemory:ChunkOverlap` | Overlapping words between chunks | `64` |
| `SemanticMemory:SimilarityThreshold` | Minimum similarity score for search results | `0.5` |

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
- **Built-in MCP Server**: `"command": "dotnet", "args": ["run", "--project", "mcp-servers/mcp-tester/mcp-tester.csproj"]`
- **Custom .NET MCP Server**: `"command": "dotnet", "args": ["run", "--project", "MyServer.csproj"]`
- **Node.js MCP Server**: `"command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"]`

## Building for Distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Make sure to include `system.md` and `mcp.json` alongside your executable if you want custom system prompts and MCP server configurations.

## Technical Details

- Built with .NET 8.0
- Uses Azure OpenAI .NET SDK (v2.2.0-beta.4)
- Semantic Kernel for RAG and text embeddings
- Model Context Protocol (MCP) SDK for tool and prompt integration
- iText7 for PDF text extraction
- Powered by Spectre.Console for rich terminal UI
- Automatic UTF-8 console setup for Windows
- Conversation history maintained in memory
- Document embeddings stored in memory (session-based)
- Supports tool calling and prompt execution via MCP servers
- Semantic search with cosine similarity matching

## License

MIT License - feel free to use and modify as needed.
