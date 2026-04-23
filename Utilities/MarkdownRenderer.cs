using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace nb.Utilities;

/// <summary>
/// Line-fed markdown renderer for terminal output. Handles headings, HRs, fenced
/// code blocks, and inline bold / code. Designed to be fed incrementally: call
/// <see cref="Append"/> with arbitrary chunks and <see cref="Finish"/> at the end.
/// </summary>
internal sealed partial class MarkdownRenderer
{
    private bool _inCodeBlock;
    private readonly StringBuilder _pending = new();

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        _pending.Append(text);

        while (true)
        {
            int nl = -1;
            for (int i = 0; i < _pending.Length; i++)
            {
                if (_pending[i] == '\n') { nl = i; break; }
            }
            if (nl < 0) break;

            var line = _pending.ToString(0, nl);
            _pending.Remove(0, nl + 1);
            RenderLine(line.TrimEnd('\r'));
        }
    }

    public void Finish()
    {
        if (_pending.Length > 0)
        {
            RenderLine(_pending.ToString().TrimEnd('\r'));
            _pending.Clear();
        }

        // Close any dangling code block so the top rule doesn't look orphaned.
        if (_inCodeBlock)
        {
            AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("grey")));
            _inCodeBlock = false;
        }
    }

    public static void Render(string markdown)
    {
        var r = new MarkdownRenderer();
        r.Append(markdown);
        r.Finish();
    }

    private void RenderLine(string line)
    {
        if (_inCodeBlock)
        {
            if (IsFence(line))
            {
                AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("grey")));
                _inCodeBlock = false;
            }
            else
            {
                AnsiConsole.WriteLine(line);
            }
            return;
        }

        if (IsFence(line))
        {
            var lang = line.TrimStart()[3..].Trim();
            var top = new Rule().RuleStyle(Style.Parse("grey")).LeftJustified();
            if (!string.IsNullOrWhiteSpace(lang))
                top.Title = $"[grey]{Markup.Escape(lang)}[/]";
            AnsiConsole.WriteLine();
            AnsiConsole.Write(top);
            _inCodeBlock = true;
            return;
        }

        if (HrPattern().IsMatch(line))
        {
            AnsiConsole.Write(new Rule().RuleStyle(Style.Parse("grey")));
            return;
        }

        var heading = HeadingPattern().Match(line);
        if (heading.Success)
        {
            var level = heading.Groups[1].Length;
            var content = ApplyInline(heading.Groups[2].Value);
            var style = level == 1 ? "bold underline" : level == 2 ? "bold" : "bold dim";
            AnsiConsole.MarkupLine($"[{style}]{content}[/]");
            return;
        }

        AnsiConsole.MarkupLine(ApplyInline(line));
    }

    private static bool IsFence(string line)
    {
        var t = line.TrimStart();
        return t.Length >= 3 && t[0] == '`' && t[1] == '`' && t[2] == '`';
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

        text = Markup.Escape(text);
        text = BoldPattern().Replace(text, "[bold]$1[/]");

        for (int i = 0; i < codes.Count; i++)
            text = text.Replace($"\x00{i}\x00", $"[cyan]{Markup.Escape(codes[i])}[/]");

        return text;
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^(\*{3,}|-{3,}|_{3,})\s*$")]
    private static partial Regex HrPattern();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodePattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();
}
