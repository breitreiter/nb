using System.Net;
using Microsoft.Extensions.Configuration;
using nb.Utilities;

namespace nb.Tests;

/// <summary>
/// bugs/Provider_Config_Cannot_Send_Extra_Headers.md — a provider entry had nowhere to
/// declare an extra HTTP header, so a gateway that authenticates its caller with its own
/// bearer token (alongside, or instead of, the upstream key) could not be described.
///
/// The assertion is about the outgoing request, so no live gateway is needed: point
/// <c>Endpoint</c> at a loopback listener and read the headers it received. Every test
/// here that asserts a header on the wire was confirmed failing before the fix — the
/// listener saw the SDK's own auth header and nothing else.
/// </summary>
public class ProviderHeaderTests
{
    /// <summary>
    /// A one-shot loopback listener that records the first request's headers and answers
    /// 400 — enough to make the SDK give up without retrying, since only a rate limit is
    /// retryable and the entry sets MaxRetries: 0 anyway.
    /// </summary>
    private sealed class CapturingListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly TaskCompletionSource<Captured> _first = new();

        public sealed record Captured(IReadOnlyDictionary<string, string> Headers, string Path);

        public string Prefix { get; }

        public CapturingListener()
        {
            var port = FreePort();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(Accept);
        }

        private async Task Accept()
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                var headers = ctx.Request.Headers.AllKeys
                    .Where(k => k is not null)
                    .ToDictionary(k => k!, k => ctx.Request.Headers[k] ?? "", StringComparer.OrdinalIgnoreCase);
                _first.TrySetResult(new Captured(headers, ctx.Request.Url?.AbsolutePath ?? ""));
                ctx.Response.StatusCode = 400;
                await using var body = ctx.Response.OutputStream;
                await body.WriteAsync("{\"error\":{\"message\":\"stop\"}}"u8.ToArray());
            }
            catch (Exception ex)
            {
                _first.TrySetException(ex);
            }
        }

        public async Task<Captured> FirstRequest()
        {
            var done = await Task.WhenAny(_first.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.Same(_first.Task, done);
            return await _first.Task;
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose() { try { _listener.Close(); } catch { } }
    }

    private static NbOptions Options()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", config, tfm, "providers") };
    }

    private static IConfiguration Entry(Dictionary<string, string?> fields)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Gw",
            ["Harness"] = "nb",
            ["ChatProviders:0:Name"] = "Gw",
            ["ChatProviders:0:MaxRetries"] = "0",
        };
        foreach (var (k, v) in fields) settings["ChatProviders:0:" + k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static async Task<CapturingListener.Captured> Reach(
        CapturingListener listener, IConfiguration config)
    {
        // The gateway answers 400, so the run fails — the outgoing request is the datum.
        var run = Task.Run(async () =>
        {
            try { await Nb.Program().Run("hi").RunAsync(config, Options()); }
            catch { }
        });

        var captured = await listener.FirstRequest();
        await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        return captured;
    }

    /// <summary>
    /// The OpenAI dialect, which is what a Cloudflare-style gateway fronts for most
    /// models. The gateway token rides alongside the SDK's own Authorization header.
    /// </summary>
    [Fact]
    public async Task LocalLlm_SendsAConfiguredHeaderAlongsideTheUpstreamKey()
    {
        using var listener = new CapturingListener();

        var request = await Reach(listener, Entry(new()
        {
            ["Provider"] = "LocalLlm",
            ["Endpoint"] = listener.Prefix + "v1",
            ["Model"] = "gw-model",
            ["ApiKey"] = "upstream-key",
            ["Headers:cf-aig-authorization"] = "Bearer gw-token",
        }));

        Assert.Equal("Bearer gw-token", request.Headers["cf-aig-authorization"]);
        Assert.Equal("Bearer upstream-key", request.Headers["Authorization"]);

        // Endpoint is a base: the SDK appends its own path. A gateway route has to
        // account for that, so pin what gets appended.
        Assert.Equal("/v1/chat/completions", request.Path);
    }

    /// <summary>
    /// The Anthropic SDK is a different assembly with a different options object, so it
    /// gets its own end-to-end check rather than a shared-helper unit test.
    /// </summary>
    [Fact]
    public async Task Anthropic_SendsAConfiguredHeaderAlongsideTheUpstreamKey()
    {
        using var listener = new CapturingListener();

        var request = await Reach(listener, Entry(new()
        {
            ["Provider"] = "Anthropic",
            ["Endpoint"] = listener.Prefix.TrimEnd('/'),
            ["Model"] = "claude-sonnet-5",
            ["ApiKey"] = "upstream-key",
            ["Headers:cf-aig-authorization"] = "Bearer gw-token",
        }));

        Assert.Equal("Bearer gw-token", request.Headers["cf-aig-authorization"]);
        Assert.Equal("upstream-key", request.Headers["x-api-key"]);

        // The Anthropic SDK appends /v1/messages to BaseUrl, so a Cloudflare AI Gateway
        // Endpoint ending in /anthropic resolves to .../anthropic/v1/messages — the
        // shape Cloudflare documents. Supplying our own HttpClient does not disturb it.
        Assert.Equal("/v1/messages", request.Path);
    }

    /// <summary>
    /// A configured header replaces an SDK default of the same name rather than being
    /// appended to it — the report's "a *different* header" case.
    /// </summary>
    [Fact]
    public async Task AConfiguredHeaderReplacesTheSdkDefaultOfTheSameName()
    {
        using var listener = new CapturingListener();

        var request = await Reach(listener, Entry(new()
        {
            ["Provider"] = "LocalLlm",
            ["Endpoint"] = listener.Prefix + "v1",
            ["Model"] = "gw-model",
            ["ApiKey"] = "upstream-key",
            ["Headers:Authorization"] = "Bearer gw-token",
        }));

        Assert.Equal("Bearer gw-token", request.Headers["Authorization"]);
    }

    /// <summary>
    /// The stored-keys mode: the gateway holds the upstream credential, so the entry has
    /// no ApiKey at all. CanCreate used to reject that outright.
    /// </summary>
    [Fact]
    public async Task AnEntryWhoseCredentialIsAHeaderNeedsNoApiKey()
    {
        using var listener = new CapturingListener();

        var request = await Reach(listener, Entry(new()
        {
            ["Provider"] = "Anthropic",
            ["Endpoint"] = listener.Prefix.TrimEnd('/'),
            ["Model"] = "claude-sonnet-5",
            ["Headers:cf-aig-authorization"] = "Bearer gw-token",
        }));

        Assert.Equal("Bearer gw-token", request.Headers["cf-aig-authorization"]);
    }

    /// <summary>
    /// Header values are ordinary config values, so they get the ${VAR} expansion every
    /// other value gets — the same convention as ApiKey, not a second mechanism.
    /// </summary>
    [Fact]
    public void HeaderValues_InterpolateEnvironmentVariables()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nb-hdr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "config.json");
            File.WriteAllText(file, """
                {"ChatProviders":[{"Name":"Gw","Headers":{"cf-aig-authorization":"Bearer ${NB_TEST_GW_TOKEN}"}}]}
                """);
            Environment.SetEnvironmentVariable("NB_TEST_GW_TOKEN", "sekrit");

            var config = ConfigurationService.BuildConfiguration(file, dir);
            ConfigurationService.ExpandEnvironmentReferences(config);

            Assert.Equal("Bearer sekrit",
                config["ChatProviders:0:Headers:cf-aig-authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_TEST_GW_TOKEN", null);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
