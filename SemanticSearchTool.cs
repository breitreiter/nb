using System.Text.Json;
using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;

namespace nb;

public class SemanticSearchTool
{
    private readonly SemanticMemoryService _semanticMemoryService;

    public SemanticSearchTool(SemanticMemoryService semanticMemoryService)
    {
        _semanticMemoryService = semanticMemoryService;
    }

    public ChatTool GetChatTool()
    {
        var parametersSchema = new
        {
            type = "object",
            properties = new
            {
                query = new
                {
                    type = "string",
                    description = "The search query to find relevant content from uploaded documents"
                },
                maxResults = new
                {
                    type = "integer",
                    description = "Maximum number of results to return (default: 3, max: 10)",
                    minimum = 1,
                    maximum = 10
                }
            },
            required = new[] { "query" }
        };

        var parameters = System.BinaryData.FromString(JsonSerializer.Serialize(parametersSchema));

        return ChatTool.CreateFunctionTool(
            functionName: "search_documents",
            functionDescription: "Search through uploaded documents to find relevant content based on semantic similarity. Use this when you need information that might be in the user's uploaded files.",
            functionParameters: parameters
        );
    }

    public async Task<string> ExecuteAsync(string query, int maxResults = 3)
    {
        // Clamp maxResults to safe bounds
        maxResults = Math.Clamp(maxResults, 1, 10);
        
        var result = await _semanticMemoryService.SearchRelevantContentAsync(query, maxResults);
        
        if (string.IsNullOrEmpty(result))
        {
            return "No relevant content found in uploaded documents.";
        }
        
        return result;
    }
}