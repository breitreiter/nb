using nb.Utilities;

namespace nb.Tests;

public class MarkdownRendererTests
{
    // Bold
    [Theory]
    [InlineData("**hello**", "[bold]hello[/]")]
    [InlineData("say **hello** world", "say [bold]hello[/] world")]
    [InlineData("**a** and **b**", "[bold]a[/] and [bold]b[/]")]
    public void ApplyInline_Bold(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.ApplyInline(input));

    // Italic markers are not rendered — asterisks pass through literally.
    [Theory]
    [InlineData("*hello*", "*hello*")]
    [InlineData("say *hello* world", "say *hello* world")]
    public void ApplyInline_ItalicNotRendered(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.ApplyInline(input));

    // Inline code — content must come through verbatim, including special chars
    [Theory]
    [InlineData("`foo`", "[cyan]foo[/]")]
    [InlineData("`[bracket]`", "[cyan][[bracket]][/]")]
    [InlineData("`**not bold**`", "[cyan]**not bold**[/]")]
    [InlineData("call `foo()` here", "call [cyan]foo()[/] here")]
    public void ApplyInline_InlineCode(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.ApplyInline(input));

    // Spectre bracket escaping — bare brackets in plain text must be escaped
    [Theory]
    [InlineData("use [brackets]", "use [[brackets]]")]
    [InlineData("a [b] c", "a [[b]] c")]
    public void ApplyInline_EscapesBracketsInPlainText(string input, string expected) =>
        Assert.Equal(expected, MarkdownRenderer.ApplyInline(input));

    // Mixed — code span containing brackets shouldn't get double-escaped
    [Fact]
    public void ApplyInline_CodeWithBracketsNotDoubleEscaped()
    {
        var result = MarkdownRenderer.ApplyInline("see `List[int]` type");
        Assert.Equal("see [cyan]List[[int]][/] type", result);
    }

    // Bold plus literal italic markers
    [Fact]
    public void ApplyInline_BoldWithLiteralAsterisks()
    {
        var result = MarkdownRenderer.ApplyInline("**bold** and *italic*");
        Assert.Equal("[bold]bold[/] and *italic*", result);
    }

    // Plain text passthrough
    [Fact]
    public void ApplyInline_PlainText_Passthrough()
    {
        Assert.Equal("hello world", MarkdownRenderer.ApplyInline("hello world"));
    }
}
