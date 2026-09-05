using Microsoft.Extensions.Configuration;

namespace nb.Providers;

/// <summary>
/// Helpers for the parts of a <c>ChatProviders</c> entry that every provider reads the
/// same way. Lives here rather than in nb.Core so an out-of-tree provider gets them too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Headers.</b> A gateway that authenticates its caller wants its own token on the
/// request — alongside the upstream key, or instead of it. An entry declares those as
/// a <c>"Headers"</c> object:
/// </para>
/// <code>
/// { "Name": "GwSonnet", "Provider": "Anthropic",
///   "Endpoint": "https://gateway.example/anthropic",
///   "ApiKey": "${ANTHROPIC_API_KEY}",
///   "Headers": { "cf-aig-authorization": "Bearer ${CF_AIG_TOKEN}" } }
/// </code>
/// <para>
/// Header values are ordinary config values, so <c>${VAR}</c> in one is expanded by the
/// config layer before a provider ever sees it — the same convention as <c>ApiKey</c>,
/// not a second mechanism. See bugs/Provider_Config_Cannot_Send_Extra_Headers.md.
/// </para>
/// </remarks>
public static class ProviderConfig
{
    public const string HeadersKey = "Headers";

    /// <summary>The entry's extra headers, empty when it declares none.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Headers(IConfiguration config) =>
        config.GetSection(HeadersKey).GetChildren()
            .Where(child => !string.IsNullOrEmpty(child.Value))
            .Select(child => new KeyValuePair<string, string>(child.Key, child.Value!))
            .ToList();

    /// <summary>
    /// An <see cref="HttpClient"/> that stamps the entry's headers on every request, or
    /// <c>null</c> when it declares none — so a provider with no headers configured keeps
    /// whatever HTTP stack its SDK sets up by default.
    /// </summary>
    public static HttpClient? HttpClientWithHeaders(IConfiguration config)
    {
        var headers = Headers(config);
        return headers.Count == 0 ? null : new HttpClient(new HeaderHandler(headers));
    }

    /// <summary>
    /// Whether the entry supplies every key the provider requires. <c>ApiKey</c> is
    /// excused when the entry carries headers: in the stored-keys gateway mode the
    /// upstream credential never leaves the gateway, so there is no key to configure.
    /// </summary>
    public static bool HasRequired(IConfiguration config, IEnumerable<string> requiredKeys)
    {
        var headers = Headers(config);
        return requiredKeys.All(key =>
            !string.IsNullOrEmpty(config[key]) ||
            (key == "ApiKey" && headers.Count > 0));
    }

    /// <summary>
    /// The entry's key, or a placeholder when a header carries the credential instead.
    /// Several SDKs refuse to construct without a non-empty key even though the value
    /// is about to be ignored by the gateway.
    /// </summary>
    public static string ApiKeyOrPlaceholder(IConfiguration config) =>
        string.IsNullOrEmpty(config["ApiKey"]) ? "gateway" : config["ApiKey"]!;

    private sealed class HeaderHandler(IReadOnlyList<KeyValuePair<string, string>> headers)
        : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var (name, value) in headers)
            {
                // Remove first: a configured header replaces the SDK's own of that name
                // rather than appending a second value to it.
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
