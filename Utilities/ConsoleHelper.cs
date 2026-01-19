namespace nb.Utilities;

/// <summary>
/// Simple console output helpers using ANSI escape codes.
/// Replaces Spectre.Console functionality with direct terminal control.
/// </summary>
public static class ConsoleHelper
{
    /// <summary>
    /// Writes a line with the specified hex color.
    /// </summary>
    public static void WriteLine(string text, string hexColor)
    {
        Console.WriteLine($"{UIColors.ToAnsiFg(hexColor)}{text}{UIColors.AnsiReset}");
    }

    /// <summary>
    /// Writes text (no newline) with the specified hex color.
    /// </summary>
    public static void Write(string text, string hexColor)
    {
        Console.Write($"{UIColors.ToAnsiFg(hexColor)}{text}{UIColors.AnsiReset}");
    }

    /// <summary>
    /// Writes a plain line without color.
    /// </summary>
    public static void WriteLine(string text) => Console.WriteLine(text);

    /// <summary>
    /// Writes plain text (no newline) without color.
    /// </summary>
    public static void Write(string text) => Console.Write(text);

    // Semantic output methods matching UIColors
    public static void WriteSuccess(string text) => WriteLine(text, UIColors.Success);
    public static void WriteError(string text) => WriteLine(text, UIColors.Error);
    public static void WriteWarning(string text) => WriteLine(text, UIColors.Warning);
    public static void WriteInfo(string text) => WriteLine(text, UIColors.Info);
    public static void WriteMuted(string text) => WriteLine(text, UIColors.Muted);
    public static void WriteAccent(string text) => WriteLine(text, UIColors.Accent);

    /// <summary>
    /// Prompts for yes/no confirmation. Returns true for yes.
    /// </summary>
    public static bool Confirm(string prompt, bool defaultValue = true)
    {
        var hint = defaultValue ? "[Y/n]" : "[y/N]";
        Write($"{prompt} {hint} ", UIColors.Info);

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(input))
            return defaultValue;

        return input.StartsWith('y');
    }

    /// <summary>
    /// Prompts for text input with optional default value.
    /// </summary>
    public static string Prompt(string prompt, string? defaultValue = null)
    {
        if (defaultValue != null)
            Write($"{prompt} [{defaultValue}]: ", UIColors.Info);
        else
            Write($"{prompt}: ", UIColors.Info);

        var input = Console.ReadLine();

        if (string.IsNullOrEmpty(input) && defaultValue != null)
            return defaultValue;

        return input ?? "";
    }

    /// <summary>
    /// Prompts for text input, allowing empty responses.
    /// </summary>
    public static string PromptAllowEmpty(string prompt)
    {
        Write($"{prompt}: ", UIColors.Info);
        return Console.ReadLine() ?? "";
    }
}
