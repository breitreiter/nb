using System.Text.Json;
using PxSharp;

namespace nb.Utilities;

/// <summary>
/// Centralized color scheme for consistent UI styling across the application.
/// </summary>
public static class UIColors
{
    private static Theme _theme = GetDefaultTheme();

    public static void LoadTheme(string themeFilePath = "theme.json")
    {
        try
        {
            if (!File.Exists(themeFilePath))
            {
                return;
            }

            var json = File.ReadAllText(themeFilePath);
            var theme = JsonSerializer.Deserialize<Theme>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (theme != null)
            {
                _theme = theme;
            }
        }
        catch
        {
            // Fall back to default theme on any error
        }
    }

    // Spectre.Console markup colors - dynamic based on theme
    public static string SpectreSuccess => _theme.Success;
    public static string SpectreError => _theme.Error;
    public static string SpectreWarning => _theme.Warning;
    public static string SpectreInfo => _theme.Info;
    public static string SpectreMuted => _theme.Muted;
    public static string SpectreAccent => _theme.Accent;
    public static string SpectreUserPrompt => _theme.UserPrompt;
    public static string SpectreFakeTool => _theme.FakeTool;

    private static Theme GetDefaultTheme() => new()
    {
        Success = "deepskyblue4_1",
        Error = "red",
        Warning = "yellow",
        Info = "white",
        Muted = "grey",
        Accent = "cadetblue_1",
        UserPrompt = "greenyellow",
        FakeTool = "mediumpurple2"
    };

    private class Theme
    {
        public string Success { get; set; } = "";
        public string Error { get; set; } = "";
        public string Warning { get; set; } = "";
        public string Info { get; set; } = "";
        public string Muted { get; set; } = "";
        public string Accent { get; set; } = "";
        public string UserPrompt { get; set; } = "";
        public string FakeTool { get; set; } = "";
    }


    /* Native Console markup colors
     * Spectre is a great library, but there are a few things it handles poorly,
     * like double-width characters and some terminal-specific features. This 
     * means we sometimes need to fall back to using native console writes.
     *
     * \u001b - The escape character (ESC)
     * [ - Start of the control sequence
     * 33 - The color code 
     * m - End marker for color commands
     * 
     * Standard ANSI color codes:
     * 30 = Black
     * 31 = Red
     * 32 = Green
     * 33 = Yellow
     * 34 = Blue
     * 35 = Magenta
     * 36 = Cyan
     * 37 = White
     * 90-97 = Bright versions (90=bright black/grey, 91=bright red, etc.)
     * 
     * Special codes:
     * 0 = Reset all formatting
     * 1 = Bold
     * 4 = Underline
     */
    public const string NativeMuted = "\u001b[90m";
    public const string NativeUserInput = "\u001b[92m";
    public const string NativeReset = "\u001b[0m";

    /*
     * Robot friend - loaded from bitmap
     */
    private static readonly Lazy<string[]> _robotLines = new(() => LoadRobotImage());

    public static string robot_img_1 => _robotLines.Value[0];
    public static string robot_img_2 => _robotLines.Value[1];
    public static string robot_img_3 => _robotLines.Value[2];

    private static string[] LoadRobotImage()
    {
        var assembly = typeof(UIColors).Assembly;
        using var stream = assembly.GetManifestResourceStream("nb.Resources.robot-logo.bmp");
        if (stream != null)
        {
            var image = PxImage.Load(stream);
            return image.GetAnsiLines();
        }

        // Fallback if resource not found
        return ["[grey58]▄▄▄▄▄[/]", "[grey50]▀▀▄▀▀[/]", "[grey58]▄▄▄▄▄[/]"];
    }
}