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
}
