using Microsoft.Extensions.Configuration;
using nb.Utilities;

namespace nb.Tests;

public class ConfigurationServiceTests : IDisposable
{
    private readonly string _tmp;

    public ConfigurationServiceTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "nb-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private string WriteJson(string dir, string activeProvider)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, $$"""{"ActiveProvider":"{{activeProvider}}"}""");
        return path;
    }

    [Fact]
    public void ExplicitConfig_IsUsed()
    {
        var file = WriteJson(_tmp, "FromFile");

        var config = ConfigurationService.BuildConfiguration(file, _tmp);

        Assert.Equal("FromFile", config["ActiveProvider"]);
    }

    [Fact]
    public void MissingExplicitConfig_Throws()
    {
        var missing = Path.Combine(_tmp, "does-not-exist.json");

        Assert.Throws<FileNotFoundException>(() =>
            ConfigurationService.BuildConfiguration(missing, _tmp));
    }

    [Fact]
    public void ProjectConfig_IsFoundByUpwardWalk_AndOverridesInstall()
    {
        // .nb/config.json at the project root; cwd is a nested subdirectory.
        WriteJson(Path.Combine(_tmp, ".nb"), "FromProject");
        var deep = Path.Combine(_tmp, "src", "deep");
        Directory.CreateDirectory(deep);

        var config = ConfigurationService.BuildConfiguration(null, deep);

        // Project layer is applied after install (the test host's appsettings.json),
        // so it wins — proving both the upward walk and later-wins precedence.
        Assert.Equal("FromProject", config["ActiveProvider"]);
    }

    [Fact]
    public void EnvVar_OverridesFile()
    {
        var file = WriteJson(_tmp, "FromFile");
        Environment.SetEnvironmentVariable("NB_ActiveProvider", "FromEnv");
        try
        {
            var config = ConfigurationService.BuildConfiguration(file, _tmp);
            Assert.Equal("FromEnv", config["ActiveProvider"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_ActiveProvider", null);
        }
    }

    [Fact]
    public void NbProviderAlias_SetsActiveProvider()
    {
        var file = WriteJson(_tmp, "FromFile");
        Environment.SetEnvironmentVariable("NB_PROVIDER", "FromAlias");
        try
        {
            var config = ConfigurationService.BuildConfiguration(file, _tmp);
            Assert.Equal("FromAlias", config["ActiveProvider"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_PROVIDER", null);
        }
    }

    [Fact]
    public void NbModelAlias_OverridesActiveProviderModelAndDeployment()
    {
        // Two providers; NB_MODEL must land on the active one's model fields only.
        var path = Path.Combine(_tmp, "config.json");
        File.WriteAllText(path, """
            {
              "ActiveProvider": "Azure",
              "ChatProviders": [
                { "Name": "Other", "Model": "keep-me" },
                { "Name": "Azure", "Model": "old", "ChatDeploymentName": "old-dep" }
              ]
            }
            """);
        Environment.SetEnvironmentVariable("NB_MODEL", "gpt-5.3-codex");
        try
        {
            var config = ConfigurationService.BuildConfiguration(path, _tmp);
            // Both keys on the active block are rewritten (classic Azure reads
            // ChatDeploymentName, everyone else reads Model).
            Assert.Equal("gpt-5.3-codex", config["ChatProviders:1:Model"]);
            Assert.Equal("gpt-5.3-codex", config["ChatProviders:1:ChatDeploymentName"]);
            // The non-active block is untouched.
            Assert.Equal("keep-me", config["ChatProviders:0:Model"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NB_MODEL", null);
        }
    }
}
