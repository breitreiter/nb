using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace nb.Shell;

public class FetchUrlTool
{
    private const int MaxContentChars = 200_000;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = Timeout
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("nb/0.9 (+https://github.com/breitreiter/nb)");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml,text/plain,application/json;q=0.9,*/*;q=0.5");
        return client;
    }

    public AIFunction CreateTool()
    {
        var fetchFunc = (string url) => FetchAsync(url);

        return AIFunctionFactory.Create(
            fetchFunc,
            name: "fetch_url",
            description: """
                Fetch the text content of a URL (HTTP/HTTPS only).
                Useful for reading API docs, READMEs, RFCs, and other web-based reference material.

                Parameters:
                - url: The full URL to fetch (must start with http:// or https://)

                Returns the text content with HTML tags stripped. Large pages are truncated.
                Requires user approval on every call.
                """
        );
    }

    public async Task<FetchUrlResult> FetchAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new FetchUrlResult(false, url, null, null, "URL is empty");

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new FetchUrlResult(false, url, null, null, "Only http:// and https:// URLs are supported");

        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

            if (!response.IsSuccessStatusCode)
                return new FetchUrlResult(false, url, (int)response.StatusCode, contentType, $"HTTP {(int)response.StatusCode} {response.StatusCode}");

            var raw = await response.Content.ReadAsStringAsync();
            var text = LooksLikeHtml(contentType, raw) ? HtmlToText(raw) : raw;

            var truncated = false;
            if (text.Length > MaxContentChars)
            {
                text = text[..MaxContentChars] + $"\n\n[truncated at {MaxContentChars} chars]";
                truncated = true;
            }

            return new FetchUrlResult(true, url, (int)response.StatusCode, contentType, null, text, truncated);
        }
        catch (TaskCanceledException)
        {
            return new FetchUrlResult(false, url, null, null, $"Request timed out after {Timeout.TotalSeconds}s");
        }
        catch (HttpRequestException ex)
        {
            return new FetchUrlResult(false, url, null, null, $"Request failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new FetchUrlResult(false, url, null, null, $"Error: {ex.Message}");
        }
    }

    private static bool LooksLikeHtml(string contentType, string body)
    {
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase)) return true;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)) return false;
        if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)) return false;
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) && !contentType.Contains("html")) return false;
        // Fall back to sniffing
        var head = body.Length > 512 ? body[..512] : body;
        return head.Contains("<html", StringComparison.OrdinalIgnoreCase) || head.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex ScriptStyleRegex = new(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t]+", RegexOptions.Compiled);
    private static readonly Regex BlankLinesRegex = new(@"\n\s*\n\s*\n+", RegexOptions.Compiled);

    private static string HtmlToText(string html)
    {
        var stripped = ScriptStyleRegex.Replace(html, "");
        stripped = TagRegex.Replace(stripped, " ");
        stripped = WebUtility.HtmlDecode(stripped);
        stripped = WhitespaceRegex.Replace(stripped, " ");
        stripped = BlankLinesRegex.Replace(stripped, "\n\n");
        return stripped.Trim();
    }
}

public record FetchUrlResult(
    bool Success,
    string Url,
    int? StatusCode,
    string? ContentType,
    string? Error,
    string? Content = null,
    bool Truncated = false);
