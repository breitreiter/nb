using Spectre.Console;
using Xunit;

namespace nb.Tests;

/// <summary>
/// Pins the mechanism the NO_COLOR gate relies on (Program.cs): forcing a
/// profile's ColorSystem to NoColors suppresses SGR color codes even when ANSI
/// is otherwise available. Spectre 0.52-preview does not read NO_COLOR itself,
/// so if a future upgrade changes this knob the gate must be revisited.
/// </summary>
public class NoColorGateTests
{
    private static (IAnsiConsole console, StringWriter sink) MakeColorConsole()
    {
        var sink = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.TrueColor,
            Out = new AnsiConsoleOutput(sink),
        });
        return (console, sink);
    }

    [Fact]
    public void ColorConsole_EmitsAnsiColor_ByDefault()
    {
        var (console, sink) = MakeColorConsole();
        console.Markup("[red]x[/]");
        Assert.Contains("\x1b[", sink.ToString());
    }

    [Fact]
    public void SettingNoColors_SuppressesAnsiColor()
    {
        var (console, sink) = MakeColorConsole();
        console.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;
        console.Markup("[red]x[/]");
        var output = sink.ToString();
        Assert.DoesNotContain("\x1b[", output);
        Assert.Contains("x", output);
    }
}
