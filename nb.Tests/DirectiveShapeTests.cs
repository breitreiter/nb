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

    // ---- `tools` vocabulary (plans/harness-emulation.md, "Vocabulary") ----

    [Fact]
    public void CanonicalToolNames_AreAccepted()
    {
        Assert.Empty(Program.CheckDirectiveShape(new TranscriptEvent[]
        {
            new ToolsEvent { Reset = true, Add = new[] { "read_file", "edit_file" } },
            new ToolsEvent { Remove = new[] { "bash", "todo" } },
        }));
    }

    /// <summary>
    /// The decided rule: `tools` speaks nb's canonical names under every costume, because
    /// it states what the run may do rather than what the model is shown. A wire name is
    /// therefore an error — and it used to be worse than an error, it was nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Edit")]          // claude-code's wire name for edit_file
    [InlineData("shell_command")] // codex's for bash
    [InlineData("run_shell_command")]
    public void ACostumesWireName_IsAnErrorAndSaysWhy(string wireName)
    {
        var errors = Program.CheckDirectiveShape(
            new TranscriptEvent[] { new ToolsEvent { Remove = new[] { wireName } } });

        var error = Assert.Single(errors);
        Assert.Contains($"unknown tool '{wireName}'", error);
        Assert.Contains("canonical", error);
        Assert.Contains("edit_file", error);  // the valid set is listed, not just asserted
    }

    /// <summary>
    /// The reason this check exists at all. An unknown name folded into the allow-set and
    /// did nothing, so a program that believed it had removed a tool still exposed it —
    /// silent for as long as the directive has existed, and found by asking the vocabulary
    /// question rather than by anything failing.
    /// </summary>
    [Fact]
    public void ATypo_IsAnError_RatherThanASilentNoOp()
    {
        Assert.Single(Program.CheckDirectiveShape(
            new TranscriptEvent[] { new ToolsEvent { Remove = new[] { "edit_flie" } } }));
    }

    /// <summary>MCP names cannot be checked this way — they depend on what connected.</summary>
    [Fact]
    public void McpServerNames_AreNotSubjectToTheNativeCheck()
    {
        Assert.Empty(Program.CheckDirectiveShape(
            new TranscriptEvent[] { new McpEvent { Add = new[] { "figma" } } }));
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
