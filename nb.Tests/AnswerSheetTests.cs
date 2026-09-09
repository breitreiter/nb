using nb;

namespace nb.Tests;

public class AnswerSheetTests
{
    private const string Sheet = """
        # Answers for the deploy exercise
        preamble that belongs to no entry

        ## deploy-target
        Staging only.
        Never touch prod.

        ## customer-name
        Acme Logistics.
        """;

    [Fact]
    public void Parse_HeadingsBecomeEntries_PreambleIsDropped()
    {
        var sheet = AnswerSheet.Parse(Sheet);
        // The H1 is an entry too — any heading level opens one; its body is the preamble text.
        Assert.Equal(new[] { "Answers for the deploy exercise", "deploy-target", "customer-name" }, sheet.Entries.Select(e => e.Id));
        Assert.Equal("Staging only.\nNever touch prod.", sheet.Entries[1].Body);
    }

    [Fact]
    public void Find_IsCaseInsensitive_AndReturnsTheAuthoredId()
        => Assert.Equal("deploy-target", AnswerSheet.Parse(Sheet).Find("Deploy-Target"));

    [Fact]
    public void Compose_JoinsSelectedBodiesVerbatim_InSheetOrder()
    {
        var sheet = AnswerSheet.Parse(Sheet);
        Assert.Equal("Staging only.\nNever touch prod.\n\nAcme Logistics.", sheet.Compose(new[] { "customer-name", "deploy-target" }));
    }

    [Fact]
    public void Parse_EmptySheet_HasNoEntries()
        => Assert.Empty(AnswerSheet.Parse("just prose, no headings").Entries);
}
