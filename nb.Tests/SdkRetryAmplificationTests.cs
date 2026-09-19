using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb.Providers;

namespace nb.Tests;

/// <summary>
/// bugs/Sdk_Retry_Policy_Multiplies_Every_Model_Call.md — the provider plugins built
/// their SDK clients from default options, so System.ClientModel installed its own
/// ClientRetryPolicy (maxRetries: 3) *below* AsIChatClient(). That layer ran to
/// completion before nb was handed an exception, which meant every nb attempt was four
/// requests on the wire that nb never learned about: pacing it could not enforce, quota
/// it could not account for, and a retry budget spent on time it did not choose.
/// </summary>
/// <remarks>
/// Driven through the real plugin — loaded from providers/ through the same
/// AssemblyLoadContext path the CLI uses — against a listener that counts requests,
/// because the defect lives in how the plugin constructs its client. A test that
/// constructed options in-process would assert the SDK's behaviour, not nb's use of it,
/// and would stay green if a provider stopped applying the policy.
/// </remarks>
public class SdkRetryAmplificationTests
{
    // Always 429s, and counts. The body is the OpenAI error shape so the SDK parses it
    // as a throttle rather than a transport fault.
    private sealed class ThrottlingEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);
        public string Prefix { get; }

        public ThrottlingEndpoint()
        {
            var port = FreePort();
            Prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Prefix);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }

                Interlocked.Increment(ref _requests);
                var body = """{"error":{"message":"Rate limit reached","type":"rate_limit_exceeded"}}"""u8.ToArray();
                ctx.Response.StatusCode = 429;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(body);
                    ctx.Response.Close();
                }
                catch { /* client hung up mid-write; the count already happened */ }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }

    // Provider plugins deploy next to the CLI binary, not the test binary, so the
    // test reaches across to the main output dir. Same shape as ProviderHeaderTests.
    private static string ProvidersDirectory()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return Path.Combine(repo, "bin", config, tfm, "providers");
    }

    // MaxRetries 0 pins nb's own layer to a single attempt, so whatever the listener
    // counts above 1 came from a retry layer nb does not control.
    private static IConfiguration ConfigFor(string endpoint) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Local",
            ["MaxRetries"] = "0",
            ["ChatProviders:0:Name"] = "Local",
            ["ChatProviders:0:Provider"] = "LocalLlm",
            ["ChatProviders:0:Endpoint"] = endpoint,
            ["ChatProviders:0:Model"] = "test-model",
            ["ChatProviders:0:ApiKey"] = "test",
        }).Build();

    [Fact]
    public async Task OneNbAttempt_IsOneRequestOnTheWire()
    {
        using var endpoint = new ThrottlingEndpoint();
        var providersDir = ProvidersDirectory();
        Assert.True(Directory.Exists(providersDir),
            $"providers/ not deployed at {providersDir} — run dotnet build first");

        var manager = new ProviderManager(providersDir);
        var config = ConfigFor(endpoint.Prefix + "v1");
        var client = manager.TryCreateChatClient(config);
        Assert.NotNull(client);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            client!.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        // Red before the fix at 4 (one call plus System.ClientModel's three).
        Assert.Equal(1, endpoint.Requests);
    }
}
