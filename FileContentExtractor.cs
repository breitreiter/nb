using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using Spectre.Console;

namespace nb;

public class FileContentExtractor
{
    public async Task<string> ExtractFileContentAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"[red]File not found: {filePath}[/]");
                return string.Empty;
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            
            // Handle PDFs
            if (extension == ".pdf")
            {
                return ExtractTextFromPdf(filePath);
            }
            
            // For everything else, try to read as text
            var content = await File.ReadAllTextAsync(filePath);
            
            // Validate it's mostly printable text
            if (IsValidTextContent(content))
            {
                return SanitizeTextContent(content);
            }
            
            throw new NotSupportedException("File appears to be binary or contains non-text content");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error reading file: {ex.Message}[/]");
            return string.Empty;
        }
    }

    private string ExtractTextFromPdf(string filePath)
    {
        try
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
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error extracting PDF text: {ex.Message}[/]");
            return string.Empty;
        }
    }

    private bool IsValidTextContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        // Count printable characters vs total characters
        int printableCount = 0;
        int totalCount = content.Length;

        foreach (char c in content)
        {
            // Allow printable ASCII, common whitespace, and Unicode letters/digits
            if (char.IsControl(c))
            {
                // Allow common control characters (newlines, tabs, carriage returns)
                if (c == '\n' || c == '\r' || c == '\t')
                {
                    printableCount++;
                }
                // Skip other control characters
            }
            else if (!char.IsControl(c))
            {
                printableCount++;
            }
        }

        // Consider it text if at least 95% of characters are printable/valid
        double printableRatio = (double)printableCount / totalCount;
        return printableRatio >= 0.95;
    }

    private string SanitizeTextContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        var sanitized = new System.Text.StringBuilder(content.Length);

        foreach (char c in content)
        {
            // Keep safe control characters: newline, tab, carriage return
            if (c == '\n' || c == '\t' || c == '\r')
            {
                sanitized.Append(c);
            }
            // Replace problematic control characters with spaces (ASCII 0-31 except the safe ones above)
            else if (char.IsControl(c))
            {
                sanitized.Append(' ');
            }
            // Keep all other characters
            else
            {
                sanitized.Append(c);
            }
        }

        return sanitized.ToString();
    }
}