using System.Text.RegularExpressions;
using Spectre.Console;

namespace nb.Utilities;

/// <summary>
/// Renders a subset of Markdown to the terminal using Spectre.Console.
/// Handles headings, bold, italic, inline code, fenced code blocks, and HRs.
/// </summary>
internal static partial class MarkdownRenderer
{
    public static void Render(string markdown)
    {
        // Split on fenced code blocks first — content inside is literal.
        // FencePattern captures (lang, code) pairs; segments interleave text/lang/code.
        var segments = FencePattern().Split(markdown);

        for (int i = 0; i < segments.Length; i++)
        {
            if (i % 3 == 0)
                RenderText(segments[i]);
            else if (i % 3 == 2)
                RenderCodeBlock(segments[i - 1], segments[i]);
            // i % 3 == 1 is the lang capture — consumed above
        }
    }

    private static void RenderText(string text)
    {
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (HrPattern().IsMatch(line))
            {
                AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("grey")));
                continue;
            }

            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                var content = ApplyInline(heading.Groups[2].Value);
                var style = level == 1 ? "bold underline" : level == 2 ? "bold" : "bold dim";
                AnsiConsole.MarkupLine($"[{style}]{content}[/]");
                continue;
            }

            AnsiConsole.MarkupLine(ApplyInline(line));
        }
    }

    private static void RenderCodeBlock(string lang, string code)
    {
        var trimmed = code.Trim('\n').TrimEnd('\r', '\n');
        var panel = new Panel(new Text(trimmed)).BorderColor(Color.Grey);
        if (!string.IsNullOrWhiteSpace(lang))
            panel.Header($"[grey]{Markup.Escape(lang.Trim())}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
    }

    internal static string ApplyInline(string text)
    {
        // Pull out inline code spans before escaping so backtick content is preserved.
        var codes = new List<string>();
        text = InlineCodePattern().Replace(text, m =>
        {
            codes.Add(m.Groups[1].Value);
            return $"\x00{codes.Count - 1}\x00";
        });

        // Escape for Spectre markup, then apply bold/italic.
        text = Markup.Escape(text);
        text = BoldPattern().Replace(text, "[bold]$1[/]");
        text = ItalicPattern().Replace(text, "[italic]$1[/]");

        // Restore inline code spans.
        for (int i = 0; i < codes.Count; i++)
            text = text.Replace($"\x00{i}\x00", $"[cyan]{Markup.Escape(codes[i])}[/]");

        return text;
    }

    [GeneratedRegex(@"```([^\n]*)\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\*{3,}|-{3,}|_{3,})\s*$")]
    private static partial Regex HrPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();

    [GeneratedRegex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)")]
    private static partial Regex ItalicPattern();
}
