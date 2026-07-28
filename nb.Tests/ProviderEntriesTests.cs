using System.Text;
using Microsoft.Extensions.Configuration;
using nb.Providers;

namespace nb.Tests;

public class ProviderEntriesTests
{
    [Fact]
    public void ReadAll_WithoutProviderField_ImplementationIsTheLabel()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            { "ChatProviders": [ { "Name": "Anthropic", "Model": "claude-3-7-sonnet" } ] }
            """));

        var entry = Assert.Single(entries);
        Assert.Equal("Anthropic", entry.Label);
        Assert.Equal("Anthropic", entry.Implementation);
        Assert.False(entry.IsAliased);
    }

    [Fact]
    public void ReadAll_TwoEntriesOnOneImplementation_BothResolve()
    {
        // The bug's fixture: one generic OpenAI-compatible client fronting two local
        // servers. Before the Provider field this was unrepresentable.
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "Name": "LocalCoder", "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1", "Model": "qwen3-coder-next" },
                { "Name": "LocalAir",   "Provider": "LocalLlm", "Endpoint": "http://127.0.0.1:8082/v1", "Model": "glm-4.5-air" }
              ]
            }
            """));

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal("LocalLlm", e.Implementation));
        Assert.All(entries, e => Assert.True(e.IsAliased));

        var coder = ProviderEntries.Find(entries, "LocalCoder")!;
        var air = ProviderEntries.Find(entries, "LocalAir")!;

        Assert.Equal("http://127.0.0.1:8081/v1", coder.Config["Endpoint"]);
        Assert.Equal("qwen3-coder-next", coder.Config["Model"]);
        Assert.Equal("http://127.0.0.1:8082/v1", air.Config["Endpoint"]);
        Assert.Equal("glm-4.5-air", air.Config["Model"]);
    }

    [Fact]
    public void ReadAll_BlankProviderField_FallsBackToLabel()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            { "ChatProviders": [ { "Name": "OpenAI", "Provider": "" } ] }
            """));

        Assert.Equal("OpenAI", Assert.Single(entries).Implementation);
    }

    [Fact]
    public void ReadAll_EntryWithoutName_IsSkipped()
    {
        // A nameless entry can never be selected, so carrying it forward would only
        // produce a confusing blank in the listings.
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "Endpoint": "http://127.0.0.1:8081/v1" },
                { "Name": "OpenAI" }
              ]
            }
            """));

        Assert.Equal("OpenAI", Assert.Single(entries).Label);
    }

    [Fact]
    public void ReadAll_CommentKeys_AreNotEntries()
    {
        // appsettings.json carries "//" comment keys inside provider entries.
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "//": "llama.cpp via llm-coder", "Name": "LocalLlm", "Model": "qwen3-coder-next" }
              ]
            }
            """));

        var entry = Assert.Single(entries);
        Assert.Equal("LocalLlm", entry.Label);
    }

    [Fact]
    public void ReadAll_MissingSection_ReturnsEmpty()
    {
        Assert.Empty(ProviderEntries.ReadAll(Config("""{ "ActiveProvider": "OpenAI" }""")));
    }

    [Fact]
    public void Find_IsCaseInsensitive()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            { "ChatProviders": [ { "Name": "LocalCoder", "Provider": "LocalLlm" } ] }
            """));

        Assert.NotNull(ProviderEntries.Find(entries, "localcoder"));
        Assert.NotNull(ProviderEntries.Find(entries, "LOCALCODER"));
    }

    [Fact]
    public void Find_UnknownLabel_ReturnsNull()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            { "ChatProviders": [ { "Name": "OpenAI" } ] }
            """));

        Assert.Null(ProviderEntries.Find(entries, "LocalCoder"));
    }

    [Fact]
    public void Find_DuplicateLabels_TakesTheFirst()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "Name": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1" },
                { "Name": "LocalLlm", "Endpoint": "http://127.0.0.1:8082/v1" }
              ]
            }
            """));

        Assert.Equal("http://127.0.0.1:8081/v1", ProviderEntries.Find(entries, "LocalLlm")!.Config["Endpoint"]);
    }

    [Fact]
    public void DuplicateLabels_ReportsCollision()
    {
        // Previously the second entry was silently dead config with no diagnostic.
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "Name": "LocalLlm", "Endpoint": "http://127.0.0.1:8081/v1" },
                { "Name": "localllm", "Endpoint": "http://127.0.0.1:8082/v1" }
              ]
            }
            """));

        Assert.Equal(new[] { "LocalLlm" }, ProviderEntries.DuplicateLabels(entries));
    }

    [Fact]
    public void DuplicateLabels_DistinctLabelsOnOneImplementation_IsNotACollision()
    {
        var entries = ProviderEntries.ReadAll(Config("""
            {
              "ChatProviders": [
                { "Name": "LocalCoder", "Provider": "LocalLlm" },
                { "Name": "LocalAir",   "Provider": "LocalLlm" }
              ]
            }
            """));

        Assert.Empty(ProviderEntries.DuplicateLabels(entries));
    }

    private static IConfiguration Config(string json) =>
        new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
}
