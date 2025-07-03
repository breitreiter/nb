# NotaBene (nb)

A simple command-line chat interface for Azure OpenAI.

## Features

- **Interactive Mode** - Run conversations in a chat loop, or pass command-line arguments as a single prompt
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

4. **Build and run**
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

## Building for Distribution

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

Make sure to include `system.md` alongside your executable if you want custom system prompts.

## Technical Details

- Built with .NET 8.0
- Uses Azure OpenAI .NET SDK (v2.2.0-beta.4)
- Powered by Spectre.Console for rich terminal UI
- Automatic UTF-8 console setup for Windows
- Conversation history maintained in memory

## License

MIT License - feel free to use and modify as needed.