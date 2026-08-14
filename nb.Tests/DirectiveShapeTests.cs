using nb.Transcript;

namespace nb.Tests;

/// <summary>
/// `Program.CheckDirectiveShape` is the config-free half of `--validate`, and the run
/// path gates on it too. The point of these tests is that agreement: a directive nb
/// cannot honour must be refused before the run, not dropped and reported after it
/// (bugs/Publishing_Nb_Into_A_Container_Is_Undocumented.md §5).
/// </summary>
public class DirectiveShapeTests
{
    private static ApprovalEvent Approval(string key, string value) => new() { Key = key, Value = value };

    [Fact]
    public void CleanProgram_HasNoErrors()
    {
        var program = new TranscriptEvent[]
        {
            Approval("default", "deny"),
            Approval("search", "allow"),
            Approval("sandbox", "none"),
            Approval("bash", "ls *"),
            new LoopEvent { Enabled = true, Threshold = 3 },
            new BudgetEvent { Key = "tokens", Value = 1000 },
            new RunEvent { Prompt = "hi" },
        };

        Assert.Empty(Program.CheckDirectiveShape(program));
    }

    // The directive from the report: `allow` is not a value `approval default` takes,
    // and the whole run went by under the dropped-to-`prompt` default before it said so.
    [Fact]
    public void ApprovalDefault_UnknownValue_IsAnError()
    {
        var errors = Program.CheckDirectiveShape(new TranscriptEvent[] { Approval("default", "allow") });

        Assert.Contains(errors, e => e.Contains("approval default 'allow'"));
    }

    [Theory]
    [InlineData("bogus", "prompt")]              // unknown key
    [InlineData("search", "deny")]               // search takes allow | prompt
    [InlineData("sandbox", "docker")]            // sandbox takes none | bwrap | bwrap-net
    public void UnhonorableApproval_IsAnError(string key, string value)
    {
        Assert.Single(Program.CheckDirectiveShape(new TranscriptEvent[] { Approval(key, value) }));
    }

    [Fact]
    public void LoopThresholdBelowTwo_IsAnError()
    {
        var errors = Program.CheckDirectiveShape(
            new TranscriptEvent[] { new LoopEvent { Enabled = true, Threshold = 1 } });

        Assert.Contains(errors, e => e.Contains("loop threshold"));
    }

    [Fact]
    public void LoopOff_IsNotSubjectToTheThresholdCheck()
    {
        Assert.Empty(Program.CheckDirectiveShape(
            new TranscriptEvent[] { new LoopEvent { Enabled = false, Threshold = 0 } }));
    }

    [Theory]
    [InlineData("bogus", 100)]
    [InlineData("tokens", 0)]
    [InlineData("tokens", -1)]
    public void BadBudget_IsAnError(string key, long value)
    {
        Assert.Single(Program.CheckDirectiveShape(
            new TranscriptEvent[] { new BudgetEvent { Key = key, Value = value } }));
    }

    [Fact]
    public void ErrorsAccumulateAcrossDirectives()
    {
        var errors = Program.CheckDirectiveShape(new TranscriptEvent[]
        {
            Approval("default", "allow"),
            Approval("search", "nope"),
            new BudgetEvent { Key = "tokens", Value = -5 },
        });

        Assert.Equal(3, errors.Count);
    }
}
