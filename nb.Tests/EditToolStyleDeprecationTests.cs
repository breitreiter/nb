using Microsoft.Extensions.Configuration;
using nb;
using nb.Harness;
using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// <c>EditToolStyle</c> is deprecated (2026-08-15): it is a proto-harness that predates
/// the idea, and `harness codex` says the same thing properly and more —
/// plans/harness-emulation.md, step 7.
///
/// The point of these tests is that deprecating is not the same as removing. The field is
/// documented and presumably in use, so behaviour is unchanged and the only new thing is
/// a warning. The behaviour half is pinned by the golden masters
/// (<see cref="ToolSurfaceGoldenTests"/>); these pin the warning, and that it is not
/// emitted at people who never set the field.
/// </summary>
[Collection(ConsoleBoundCollection.Name)]
public class EditToolStyleDeprecationTests
{
    [Fact]
    public async Task SettingApplyPatch_WarnsAndNamesTheDirectiveThatReplacesIt()
    {
        var result = await Run(editToolStyle: "ApplyPatch");

        var warning = Assert.Single(result.Warnings, w => w.StartsWith("EditToolStyle is deprecated"));
        Assert.Contains("'Mock'", warning);          // findable in a layered config
        Assert.Contains("harness codex", warning);   // and pointed at its replacement
    }

    /// <summary>
    /// The redundant setting gets different advice: `EditReplace` is the default, so the
    /// migration is deletion, not a directive. Telling that user to adopt a costume would
    /// be wrong — they are not asking for one.
    /// </summary>
    [Fact]
    public async Task SettingTheDefaultExplicitly_IsToldToJustRemoveIt()
    {
        var result = await Run(editToolStyle: "EditReplace");

        var warning = Assert.Single(result.Warnings, w => w.StartsWith("EditToolStyle is deprecated"));
        Assert.Contains("already the default", warning);
        Assert.DoesNotContain("harness codex", warning);
    }

    [Fact]
    public async Task NotSettingIt_SaysNothing()
    {
        var result = await Run(editToolStyle: null);

        Assert.DoesNotContain(result.Warnings, w => w.Contains("EditToolStyle"));
    }

    /// <summary>
    /// Deprecated, not disabled. A warning that came with a silent behaviour change would
    /// be the worst of both — the run has to keep meaning what it meant.
    /// </summary>
    /// <summary>
    /// Deprecated, not disabled. A warning that came with a silent behaviour change would
    /// be the worst of both — the run has to keep meaning what it meant. Asserted against
    /// the advertised surface the runtime actually assembles, since that is the wiring a
    /// deprecation is most likely to break on the way to removal.
    /// </summary>
    [Theory]
    [InlineData("ApplyPatch", "apply_patch", "edit_file")]
    [InlineData("EditReplace", "edit_file", "apply_patch")]
    public async Task TheDeprecatedSettingStillTakesEffect(string style, string advertised, string withheld)
    {
        using var runtime = await NbRuntime.BuildAsync(ConfigWith(style), ProvidersOptions());
        var names = runtime.Conversation.Harness.CreateTools(ToolSurface.All).Select(t => t.Name).ToList();

        Assert.Contains(advertised, names);
        Assert.DoesNotContain(withheld, names);
    }

    // ---- harness ----

    private static Task<RunResult> Run(string? editToolStyle, string prompt = "hello") =>
        Nb.Program().Run(prompt).RunAsync(ConfigWith(editToolStyle), ProvidersOptions());

    private static IConfiguration ConfigWith(string? editToolStyle)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ActiveProvider"] = "Mock",
            ["ChatProviders:0:Name"] = "Mock",
            ["ChatProviders:0:Response"] = "OK",
        };
        if (editToolStyle != null) settings["ChatProviders:0:EditToolStyle"] = editToolStyle;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    // nb's providers live next to nb's Exe, not next to the test assembly.
    private static NbOptions ProvidersOptions()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var configuration = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        var repo = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
        return new NbOptions { ProvidersDirectory = Path.Combine(repo, "bin", configuration, tfm, "providers") };
    }
}
