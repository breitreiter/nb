using nb.MCP;

namespace nb.Tests;

public class ResolveHeadersTests
{
    [Fact]
    public void NullHeaders_ReturnsNull()
    {
        Assert.Null(McpManager.ResolveHeaders(null));
    }

    [Fact]
    public void EmptyHeaders_ReturnsNull()
    {
        Assert.Null(McpManager.ResolveHeaders(new Dictionary<string, string>()));
    }

    [Fact]
    public void LiteralValue_PassesThroughUnchanged()
    {
        var result = McpManager.ResolveHeaders(new Dictionary<string, string>
        {
            ["X-Api-Key"] = "static-secret"
        });

        Assert.NotNull(result);
        Assert.Equal("static-secret", result!["X-Api-Key"]);
    }

    [Fact]
    public void EnvReference_IsInterpolated()
    {
        Environment.SetEnvironmentVariable("NB_TEST_MCP_TOKEN", "abc123");
        try
        {
            var result = McpManager.ResolveHeaders(new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer ${NB_TEST_MCP_TOKEN}"
            });

            Assert.Equal("Bearer abc123", result!["Authorization"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_TEST_MCP_TOKEN", null);
        }
    }

    [Fact]
    public void MultipleReferences_InOneValue_AreAllInterpolated()
    {
        Environment.SetEnvironmentVariable("NB_TEST_A", "one");
        Environment.SetEnvironmentVariable("NB_TEST_B", "two");
        try
        {
            var result = McpManager.ResolveHeaders(new Dictionary<string, string>
            {
                ["X-Combined"] = "${NB_TEST_A}-${NB_TEST_B}"
            });

            Assert.Equal("one-two", result!["X-Combined"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_TEST_A", null);
            Environment.SetEnvironmentVariable("NB_TEST_B", null);
        }
    }

    [Fact]
    public void UnsetEnvReference_ResolvesToEmptyString()
    {
        Environment.SetEnvironmentVariable("NB_TEST_MISSING", null);

        var result = McpManager.ResolveHeaders(new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer ${NB_TEST_MISSING}"
        });

        Assert.Equal("Bearer ", result!["Authorization"]);
    }

    [Fact]
    public void HeaderKeys_AreNotInterpolated()
    {
        Environment.SetEnvironmentVariable("NB_TEST_KEYVAR", "resolved");
        try
        {
            var result = McpManager.ResolveHeaders(new Dictionary<string, string>
            {
                ["${NB_TEST_KEYVAR}"] = "value"
            });

            Assert.True(result!.ContainsKey("${NB_TEST_KEYVAR}"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_TEST_KEYVAR", null);
        }
    }
}

public class McpLayerMergeTests : IDisposable
{
    private readonly string _tmp;

    public McpLayerMergeTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "nb-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string Write(string name, string json)
    {
        var path = Path.Combine(_tmp, name);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void LaterLayerAddsAndOverridesByName()
    {
        var install = Write("install.json", """{"mcpServers":{"a":{"command":"install-a"},"b":{"command":"install-b"}}}""");
        var project = Write("project.json", """{"mcpServers":{"b":{"command":"project-b"},"c":{"command":"project-c"}}}""");

        var merged = McpManager.MergeLayers(new[] { install, project });

        Assert.Equal(3, merged.McpServers.Count);
        Assert.Equal("install-a", merged.McpServers["a"].Command);
        Assert.Equal("project-b", merged.McpServers["b"].Command); // later layer wins
        Assert.Equal("project-c", merged.McpServers["c"].Command);
    }

    [Fact]
    public void MissingAndNullLayersAreSkipped()
    {
        var install = Write("install.json", """{"mcpServers":{"a":{"command":"a"}}}""");

        var merged = McpManager.MergeLayers(new[] { install, null, Path.Combine(_tmp, "absent.json") });

        Assert.Single(merged.McpServers);
        Assert.Equal("a", merged.McpServers["a"].Command);
    }

    [Fact]
    public void NoLayers_YieldsEmptyConfig()
    {
        var merged = McpManager.MergeLayers(new string?[] { null, null });
        Assert.Empty(merged.McpServers);
        Assert.Empty(merged.Servers);
    }

    // bugs/Missing_Mcp_Manifest_Silently_Ignored.md — an explicit --mcp manifest is
    // strict where the layered lookup is lenient: the caller named that one file, so
    // a bad path must not degrade into "no servers configured".

    [Fact]
    public void ExplicitManifest_Missing_Throws()
    {
        var absent = Path.Combine(_tmp, "absent.json");
        var ex = Assert.Throws<FileNotFoundException>(() => McpManager.ReadExplicitManifest(absent));
        Assert.Contains(absent, ex.Message);
    }

    [Fact]
    public void ExplicitManifest_MissingDirectory_Throws()
    {
        Assert.Throws<FileNotFoundException>(
            () => McpManager.ReadExplicitManifest("/nonexistent/dir/mcp.json"));
    }

    [Fact]
    public void ExplicitManifest_Directory_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => McpManager.ReadExplicitManifest(_tmp));
    }

    [Fact]
    public void ExplicitManifest_Malformed_Throws()
    {
        var bad = Write("bad.json", """{"mcpServers":{ oops""");
        var ex = Assert.Throws<InvalidOperationException>(() => McpManager.ReadExplicitManifest(bad));
        Assert.Contains(bad, ex.Message);
    }

    [Fact]
    public void ExplicitManifest_Valid_Loads()
    {
        var ok = Write("ok.json", """{"mcpServers":{"a":{"command":"a"}}}""");
        Assert.Equal("a", McpManager.ReadExplicitManifest(ok).McpServers["a"].Command);
    }

    [Fact]
    public void LoadConfig_ExplicitMissingManifest_Throws()
    {
        using var mcp = new McpManager();
        Assert.Throws<FileNotFoundException>(() => mcp.LoadConfig(Path.Combine(_tmp, "absent.json")));
    }
}

// bugs/Missing_Mcp_Manifest_Silently_Ignored.md gap 2 — a server named by
// `mcp +name` that is absent from the manifest is never attempted, so it never
// reaches FailedServers. The gate has to check the config, not just the failures.
public class McpAssertServersAvailableTests : IDisposable
{
    private readonly string _tmp;
    private readonly McpManager _mcp = new();

    public McpAssertServersAvailableTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "nb-mcp-assert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        var manifest = Path.Combine(_tmp, "mcp.json");
        File.WriteAllText(manifest, """{"mcpServers":{"configured":{"command":"true"}}}""");
        _mcp.LoadConfig(manifest);
    }

    public void Dispose()
    {
        _mcp.Dispose();
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    [Fact]
    public void NamedButNotConfigured_Throws()
    {
        var ex = Assert.Throws<McpServerUnavailableException>(
            () => _mcp.AssertServersAvailable(new[] { "no-such-server" }));
        Assert.Contains("not configured", ex.Message);
        Assert.Contains("no-such-server", ex.Message);
    }

    [Fact]
    public void NamedAndConfigured_DoesNotThrow()
    {
        _mcp.AssertServersAvailable(new[] { "configured" });
    }

    [Fact]
    public void NullSurface_AssertsNothing()
    {
        _mcp.AssertServersAvailable(null);
    }

    // Strict-empty is the documented default: a program that names no servers runs
    // fine with no servers configured (docs/conversation-program-cli.md:397).
    [Fact]
    public void EmptySurface_WithEmptyConfig_DoesNotThrow()
    {
        using var empty = new McpManager();
        empty.AssertServersAvailable(Array.Empty<string>());
    }
}
