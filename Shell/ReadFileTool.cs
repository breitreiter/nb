using System.Text;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Microsoft.Extensions.AI;

namespace nb.Shell;

public class ReadFileTool
{
    private const int DefaultMaxLines = 2000;
    private const int MaxImageSizeBytes = 20 * 1024 * 1024; // 20MB

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    private readonly ShellEnvironment _env;

    public ReadFileTool(ShellEnvironment env)
    {
        _env = env;
    }

    public AIFunction CreateTool()
    {
        var readFunc = (string path, int? offset, int? limit) =>
            ReadFile(path, offset, limit);

        return AIFunctionFactory.Create(
            readFunc,
            name: "read_file",
            description: $"""
                Read the contents of a file and return it with line numbers.
                Paths are relative to: {_env.ShellCwd}

                Supported file types:
                - Text files: returned with line numbers
                - PDF files (.pdf): text is extracted and returned
                - Image files (.jpg, .jpeg, .png): returned as base64-encoded data for vision models

                Parameters:
                - path: File path (absolute or relative to working directory)
                - offset: 1-based line number to start reading from (default: 1, text files only)
                - limit: Maximum number of lines to return (default: {DefaultMaxLines}, text files only)

                Use this instead of bash cat/head/tail for reading files.
                For large text files, use offset and limit to read specific sections.
                """
        );
    }

    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(_env.ShellCwd, path));

    public ReadFileResult ReadFile(string path, int? offset = null, int? limit = null)
    {
        try
        {
            var fullPath = ResolvePath(path);

            if (!File.Exists(fullPath))
                return new ReadFileResult(false, fullPath, null, 0, 0, $"File not found: {fullPath}");

            var ext = Path.GetExtension(fullPath).ToLowerInvariant();

            if (ext == ".pdf")
                return ReadPdf(fullPath);

            if (ImageExtensions.Contains(ext))
                return ReadImage(fullPath);

            return ReadTextFile(fullPath, offset, limit);
        }
        catch (Exception ex)
        {
            return new ReadFileResult(false, path, null, 0, 0, ex.Message);
        }
    }

    private ReadFileResult ReadTextFile(string fullPath, int? offset, int? limit)
    {
        var allLines = File.ReadAllLines(fullPath);
        var totalLines = allLines.Length;

        var startLine = Math.Max(1, offset ?? 1);
        var maxLines = limit ?? DefaultMaxLines;

        if (startLine > totalLines)
            return new ReadFileResult(true, fullPath, "", totalLines, 0, null);

        var lines = allLines
            .Skip(startLine - 1)
            .Take(maxLines)
            .Select((line, i) => $"{startLine + i}: {line}");

        var content = string.Join("\n", lines);
        var linesReturned = Math.Min(maxLines, totalLines - startLine + 1);
        var truncated = startLine + linesReturned - 1 < totalLines;

        string? truncationNote = truncated
            ? $"[Showing lines {startLine}-{startLine + linesReturned - 1} of {totalLines}. Use offset/limit to read more.]"
            : null;

        if (truncationNote != null)
            content = $"{content}\n{truncationNote}";

        return new ReadFileResult(true, fullPath, content, totalLines, linesReturned, null);
    }

    private ReadFileResult ReadPdf(string fullPath)
    {
        using var reader = new PdfReader(fullPath);
        using var document = new PdfDocument(reader);

        var pages = new List<string>();
        var pageCount = document.GetNumberOfPages();
        for (int i = 1; i <= pageCount; i++)
        {
            var page = document.GetPage(i);
            var strategy = new SimpleTextExtractionStrategy();
            pages.Add(PdfTextExtractor.GetTextFromPage(page, strategy));
        }

        var content = string.Join("\n", pages);
        return new ReadFileResult(true, fullPath, content, pageCount, pageCount, null) { FileType = "pdf" };
    }

    private ReadFileResult ReadImage(string fullPath)
    {
        var fileInfo = new FileInfo(fullPath);
        if (fileInfo.Length > MaxImageSizeBytes)
            return new ReadFileResult(false, fullPath, null, 0, 0,
                $"Image file size ({fileInfo.Length:N0} bytes) exceeds {MaxImageSizeBytes / (1024 * 1024)}MB limit.");

        var imageData = File.ReadAllBytes(fullPath);
        var base64 = Convert.ToBase64String(imageData);
        var mimeType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };

        return new ReadFileResult(true, fullPath, null, 0, 0, null)
        {
            FileType = "image",
            ImageBase64 = base64,
            MimeType = mimeType,
            ImageSizeBytes = fileInfo.Length
        };
    }
}

public record ReadFileResult(
    bool Success,
    string Path,
    string? Content,
    int TotalLines,
    int LinesReturned,
    string? Error)
{
    public string FileType { get; init; } = "text";
    public string? ImageBase64 { get; init; }
    public string? MimeType { get; init; }
    public long ImageSizeBytes { get; init; }
}
