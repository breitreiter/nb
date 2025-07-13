namespace nb;

public interface ISemanticMemoryService
{
    Task InitializeAsync();
    Task<bool> UploadFileAsync(string filePath);
    Task<string> SearchRelevantContentAsync(string query, int maxResults = 3);
}