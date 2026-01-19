using System.Text.Json;
using PxSharp;

namespace nb.Utilities;

/// <summary>
/// Centralized color scheme for consistent UI styling across the application.
/// Colors are stored as hex codes (e.g., "#E06C75") for portability across
/// different rendering backends (ANSI, Terminal.Gui, etc.)
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

    // Semantic colors - hex format for portability
    public static string Success => _theme.Success;
    public static string Error => _theme.Error;
    public static string Warning => _theme.Warning;
    public static string Info => _theme.Info;
    public static string Muted => _theme.Muted;
    public static string Accent => _theme.Accent;
    public static string UserPrompt => _theme.UserPrompt;
    public static string FakeTool => _theme.FakeTool;

    private static Theme GetDefaultTheme() => new()
    {
        Success = "#005F87",    // Deep sky blue
        Error = "#E06C75",      // Soft red
        Warning = "#E5C07B",    // Warm yellow
        Info = "#ABB2BF",       // Light gray (not pure white)
        Muted = "#5C6370",      // Medium gray
        Accent = "#56B6C2",     // Cyan/teal
        UserPrompt = "#98C379", // Green
        FakeTool = "#C678DD"    // Purple
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


    // ANSI escape code helpers
    public const string AnsiReset = "\u001b[0m";

    /// <summary>
    /// Converts a hex color (e.g., "#E06C75") to an ANSI 24-bit foreground escape sequence.
    /// </summary>
    public static string ToAnsiFg(string hexColor)
    {
        var (r, g, b) = ParseHex(hexColor);
        return $"\u001b[38;2;{r};{g};{b}m";
    }

    /// <summary>
    /// Converts a hex color to an ANSI 24-bit background escape sequence.
    /// </summary>
    public static string ToAnsiBg(string hexColor)
    {
        var (r, g, b) = ParseHex(hexColor);
        return $"\u001b[48;2;{r};{g};{b}m";
    }

    private static (int r, int g, int b) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6) return (128, 128, 128); // fallback gray
        return (
            Convert.ToInt32(hex[..2], 16),
            Convert.ToInt32(hex[2..4], 16),
            Convert.ToInt32(hex[4..6], 16)
        );
    }

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

        // Fallback if resource not found (plain ASCII)
        return ["[nb]", "   ", "[nb]"];
    }
}