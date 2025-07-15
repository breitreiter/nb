namespace nb;

// Interface to support future storage implementations (persistent vector stores, different backends)
public interface ISemanticMemoryService
{
    Task InitializeAsync();
    Task<bool> UploadFileAsync(string filePath);
    Task<string> SearchRelevantContentAsync(string query, int maxResults = 3);
}