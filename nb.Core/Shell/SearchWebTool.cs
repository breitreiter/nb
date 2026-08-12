using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace nb.Shell;

/// <summary>
/// The <c>search_web</c> tool. nb is a diagnostic instrument, so the point of this
/// tool is that a model can <em>ask</em> to search and have that ask land in the
/// transcript as a <c>ToolCallEvent</c> — see plans/web-search.md. Whether a query
/// actually runs is a separate, opt-in concern (<c>Search.Provider</c>).
///
/// Declared-only (the default) advertises the tool and returns a plain, non-error
/// note that no backend is wired. It must stay non-error: ExitReasons.ToolErrorLimit
/// aborts a turn once tool errors accumulate, which would kill exactly the runs where
/// the model tries hardest to search and misfile them as tool_error_limit.
/// </summary>
public class SearchWebTool
{
    private const int ResultCount = 5;   // fixed on purpose — result-count tuning is a non-goal
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static readonly HttpClient _http = CreateClient();

    private readonly string? _braveApiKey;   // null => declared-only

    public SearchWebTool(string? braveApiKey = null) => _braveApiKey = braveApiKey;

    /// <summary>True when a live backend will actually be queried.</summary>
    public bool HasBackend => !string.IsNullOrWhiteSpace(_braveApiKey);

    /// <summary>
    /// Build from the <c>Search</c> config block. Throws for a configured provider
    /// with no key rather than degrading to declared-only: a silent downgrade would
    /// make a run look like intent-capture when its author believed it was live, and
    /// the transcript would then mean something other than what its reader thinks.
    /// </summary>
    public static SearchWebTool FromConfig(string? provider, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Equals("none", StringComparison.OrdinalIgnoreCase))
            return new SearchWebTool();

        if (!provider.Equals("brave", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"invalid Search.Provider '{provider}'. Valid: none, brave.");

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Search.Provider is 'brave' but Search.ApiKey is empty. Supply a key, or set Provider to 'none' to declare search_web without a backend.");

        return new SearchWebTool(apiKey);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("nb/0.9 (+https://github.com/breitreiter/nb)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // Deliberately says nothing about the backend possibly being absent. Telling the
    // model the tool might be dead would suppress the calls we exist to count.
    public AIFunction CreateTool()
    {
        var searchFunc = (string query) => SearchAsync(query);

        return AIFunctionFactory.Create(
            searchFunc,
            name: "search_web",
            description: """
                Search the web and return a list of results (title, URL, and a short snippet).

                Use this when answering depends on information you may not have: current events,
                recent releases, version-specific behavior, prices, someone's current role, or
                anything the user implies is time-sensitive. Prefer searching over answering from
                memory whenever being out of date would change the answer. For open-ended research
                requests, begin searching rather than asking a scoping question first.

                Parameters:
                - query: The search query. Keywords generally work better than a full sentence.

                Returns results as title, URL, and snippet. Snippets are short — to read a page,
                pass its URL to fetch_url.
                Requires user approval on every call.
                """
        );
    }

    public async Task<SearchWebResult> SearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new SearchWebResult(false, query, null, "Query is empty");

        if (!HasBackend)
            return new SearchWebResult(true, query, DeclaredOnlyNote, null);

        try
        {
            var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={ResultCount}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Subscription-Token", _braveApiKey);

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return new SearchWebResult(false, query, null, $"Brave Search returned {(int)response.StatusCode} {response.ReasonPhrase}");

            var json = await response.Content.ReadAsStringAsync();
            return new SearchWebResult(true, query, FormatBraveResults(json), null);
        }
        catch (TaskCanceledException)
        {
            return new SearchWebResult(false, query, null, $"Search timed out after {Timeout.TotalSeconds:N0}s");
        }
        catch (Exception ex)
        {
            return new SearchWebResult(false, query, null, ex.Message);
        }
    }

    /// <summary>
    /// Terse by design. It states the configuration fact and stops — no instruction not
    /// to invent results. Suppressing fabrication would make nb steer the behavior it
    /// exists to observe; instead fabrication stays visible, and stays distinguishable,
    /// because invented results land as assistant text while only real tool execution
    /// produces a ToolResultEvent.
    /// </summary>
    public const string DeclaredOnlyNote =
        "No search backend is configured, so no query was performed. This is a configuration " +
        "state, not a search failure: the call was well-formed and has been recorded.";

    private static string FormatBraveResults(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("web", out var web) ||
            !web.TryGetProperty("results", out var results) ||
            results.GetArrayLength() == 0)
        {
            return "No results.";
        }

        var sb = new StringBuilder();
        int n = 0;
        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var link = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            var snippet = r.TryGetProperty("description", out var d) ? d.GetString() : null;

            sb.Append(++n).Append(". ").Append(title ?? "(untitled)").Append('\n');
            if (!string.IsNullOrWhiteSpace(link)) sb.Append("   ").Append(link).Append('\n');
            if (!string.IsNullOrWhiteSpace(snippet)) sb.Append("   ").Append(StripTags(snippet)).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    // Brave marks query-term hits with <strong> and HTML-escapes the surrounding text,
    // so drop the markup and decode entities — otherwise snippets reach the model full
    // of &#x27; and &amp;, which is noise in a transcript people read as evidence.
    private static string StripTags(string s) =>
        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", string.Empty));
}

public record SearchWebResult(bool Success, string Query, string? Output, string? Error);
