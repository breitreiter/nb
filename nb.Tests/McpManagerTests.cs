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
