using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Embeddings;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Spectre.Console;

namespace nb;

public class SemanticMemoryService : ISemanticMemoryService
{
    private Kernel? _kernel;
    private ITextEmbeddingGenerationService? _embeddingService;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly string _deploymentName;
    private readonly List<DocumentChunk> _documents = new();
    private readonly int _chunkSize;
    private readonly int _chunkOverlap;
    private readonly double _similarityThreshold;

    public SemanticMemoryService(string endpoint, string apiKey, string deploymentName, int chunkSize, int chunkOverlap, double similarityThreshold)
    {
        _endpoint = endpoint;
        _apiKey = apiKey;
        _deploymentName = deploymentName;
        _chunkSize = chunkSize;
        _chunkOverlap = chunkOverlap;
        _similarityThreshold = similarityThreshold;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var kernelBuilder = Kernel.CreateBuilder();
            kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(
                _deploymentName,
                _endpoint,
                _apiKey);
            
            _kernel = kernelBuilder.Build();
            _embeddingService = _kernel.GetRequiredService<ITextEmbeddingGenerationService>();
            
            AnsiConsole.MarkupLine("[green]Semantic memory initialized successfully[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to initialize semantic memory: {ex.Message}[/]");
            throw;
        }
    }

    public async Task<bool> UploadFileAsync(string filePath)
    {
        if (_embeddingService == null)
        {
            AnsiConsole.MarkupLine("[red]Semantic memory not initialized[/]");
            return false;
        }

        try
        {
            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[red]File not found: {filePath}[/]");
                return false;
            }

            AnsiConsole.MarkupLine($"[yellow]Processing file: {Path.GetFileName(filePath)}[/]");

            string content = await ExtractTextFromFileAsync(filePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                AnsiConsole.MarkupLine("[red]No text content extracted from file[/]");
                return false;
            }

            var chunks = ChunkText(content);
            var fileName = Path.GetFileName(filePath);

            for (int i = 0; i < chunks.Count; i++)
            {
                var embedding = await _embeddingService.GenerateEmbeddingAsync(chunks[i]);
                
                var documentChunk = new DocumentChunk
                {
                    Id = $"{fileName}_chunk_{i}",
                    Text = chunks[i],
                    FileName = fileName,
                    ChunkIndex = i,
                    TotalChunks = chunks.Count,
                    FilePath = filePath,
                    Embedding = embedding.ToArray()
                };

                _documents.Add(documentChunk);
            }

            AnsiConsole.MarkupLine($"[green]Successfully uploaded {fileName} ({chunks.Count} chunks)[/]");
            return true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error uploading file: {ex.Message}[/]");
            return false;
        }
    }

    public async Task<string> SearchRelevantContentAsync(string query, int maxResults = 3)
    {
        if (_embeddingService == null || !_documents.Any())
        {
            return string.Empty;
        }

        try
        {
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query);
            var queryVector = queryEmbedding.ToArray();

            var similarities = _documents
                .Select(doc => new { Document = doc, Similarity = CosineSimilarity(queryVector, doc.Embedding) })
                .OrderByDescending(x => x.Similarity)
                .Take(maxResults)
                .Where(x => x.Similarity > _similarityThreshold)
                .ToList();

            if (!similarities.Any())
            {
                return string.Empty;
            }

            var relevantContent = similarities
                .Select(s => $"[From {s.Document.FileName}]\n{s.Document.Text}")
                .ToList();

            return string.Join("\n\n---\n\n", relevantContent);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error searching memory: {ex.Message}[/]");
            return string.Empty;
        }
    }

    private static double CosineSimilarity(float[] vectorA, float[] vectorB)
    {
        if (vectorA.Length != vectorB.Length)
            return 0;

        double dotProduct = 0;
        double magnitudeA = 0;
        double magnitudeB = 0;

        for (int i = 0; i < vectorA.Length; i++)
        {
            dotProduct += vectorA[i] * vectorB[i];
            magnitudeA += vectorA[i] * vectorA[i];
            magnitudeB += vectorB[i] * vectorB[i];
        }

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0;

        return dotProduct / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
    }

    private async Task<string> ExtractTextFromFileAsync(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        return extension switch
        {
            ".pdf" => ExtractTextFromPdf(filePath),
            ".txt" => await File.ReadAllTextAsync(filePath),
            ".md" => await File.ReadAllTextAsync(filePath),
            _ => throw new NotSupportedException($"File type {extension} is not supported")
        };
    }

    private string ExtractTextFromPdf(string filePath)
    {
        using var reader = new PdfReader(filePath);
        using var document = new PdfDocument(reader);
        
        var text = new List<string>();
        for (int i = 1; i <= document.GetNumberOfPages(); i++)
        {
            var page = document.GetPage(i);
            var strategy = new SimpleTextExtractionStrategy();
            var pageText = PdfTextExtractor.GetTextFromPage(page, strategy);
            text.Add(pageText);
        }
        
        return string.Join("\n", text);
    }

    private List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        for (int i = 0; i < words.Length; i += _chunkSize - _chunkOverlap)
        {
            var chunkWords = words.Skip(i).Take(_chunkSize).ToArray();
            var chunk = string.Join(" ", chunkWords);
            
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                chunks.Add(chunk.Trim());
            }
        }
        
        return chunks;
    }
}

public class DocumentChunk
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public int TotalChunks { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public float[] Embedding { get; set; } = Array.Empty<float>();
}