namespace nb;

/// <summary>
/// Centralized color scheme for consistent UI styling across the application.
/// </summary>
public static class UIColors
{
    // Spectre.Console markup colors
    public const string SpectreSuccess = "deepskyblue4_1";
    public const string SpectreError = "red";
    public const string SpectreWarning = "yellow";
    public const string SpectreInfo = "white";
    public const string SpectreMuted = "grey";
    public const string SpectreAccent = "cadetblue_1";
    public const string SpectreUserPrompt = "greenyellow";
    public const string SpectreFakeTool = "mediumpurple2";


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
    public const string NativeSuccess = "\u001b[32m";
    public const string NativeError = "\u001b[31m";
    public const string NativeWarning = "\u001b[33m";
    public const string NativeInfo = "\u001b[37m";
    public const string NativeMuted = "\u001b[90m";
    public const string NativeUserInput = "\u001b[92m";
    public const string NativeReset = "\u001b[0m";
}