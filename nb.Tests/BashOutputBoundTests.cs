using nb.Shell;

namespace nb.Tests;

/// <summary>
/// The bash tool retains a bounded slice of a command's output as it arrives, rather than
/// accumulating every line and discarding all but ~70 of them after the process exits
/// (bugs/Bash_Buffers_Unbounded_Output_Before_Truncating.md).
///
/// <para>Honest labelling, per the repo's test-first rule: most of these assert the *fixed*
/// behaviour rather than reproducing the bug. The original failure is a 20 MB/s runaway
/// producer driven to OutOfMemoryException, which is not reproducible at proportionate cost,
/// and the report's suggested GC.GetTotalAllocatedBytes proxy does not work — it counts
/// cumulative allocation including garbage, and a line is still allocated per line read
/// either way. What *is* directly observable is the byte ceiling, which is new behaviour and
/// was confirmed failing beforehand.</para>
/// </summary>
public class BashOutputBoundTests
{
    private static BashTool Tool(long ceiling = 8L * 1024 * 1024) =>
        new(ShellEnvironment.Detect(Array.Empty<string>()),
            defaultTimeoutSeconds: 120,
            outputByteCeiling: ceiling);

    private static Task<ShellResult> Run(BashTool tool, string command) =>
        tool.ExecuteAsync(command, cwd: Path.GetTempPath());

    // --- The untruncated path is unchanged ---

    [Fact]
    public async Task SmallOutput_IsReturnedWhole()
    {
        var result = await Run(Tool(), "printf 'a\\nb\\nc\\n'");
        Assert.Equal("a\nb\nc", result.Stdout);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task OutputAtTheThresholdEdge_IsStillWhole()
    {
        // 200 lines is exactly _outputThresholdLines; every line must still be present,
        // which is what forces the retained head to be sized off the threshold rather
        // than off the sandwich's 50-line head.
        var result = await Run(Tool(), "seq 1 200");
        Assert.False(result.Truncated);
        Assert.Equal(200, result.Stdout.Split('\n').Length);
        Assert.StartsWith("1\n2\n", result.Stdout);
        Assert.EndsWith("\n199\n200", result.Stdout);
    }

    // --- The sandwich still says the right thing, from a buffer that no longer holds it ---

    [Fact]
    public async Task LargeOutput_KeepsHeadAndTailAndCountsTheRest()
    {
        var result = await Run(Tool(), "seq 1 100000");

        Assert.True(result.Truncated);
        // Head: the first 50 lines.
        Assert.StartsWith("1\n2\n3\n", result.Stdout);
        // Tail: the last 20. This is the assertion that actually exercises the ring —
        // these lines were dropped and re-dropped ~100k times as they streamed past.
        Assert.EndsWith("\n99999\n100000", result.Stdout);
        // Omitted count is exact despite nothing in between being retained.
        Assert.Contains($"[... {100000 - 50 - 20} lines omitted", result.Stdout);
    }

    [Fact]
    public async Task ReportedTotal_StaysExactAcrossDroppedLines()
    {
        // seq 1 100000 emits sum of digit-lengths + one newline each = 588895 bytes.
        var expected = Enumerable.Range(1, 100000).Sum(i => i.ToString().Length + 1);
        var result = await Run(Tool(), "seq 1 100000");
        Assert.Contains($"({expected / 1024.0:F1} KB total)", result.Stdout);
    }

    // --- The byte ceiling: new behaviour, and the one observable red ---

    [Fact]
    public async Task RunawayProducer_HitsTheCeilingAndIsKilled()
    {
        // `yes` never terminates. Before the ceiling existed this ran until the 120s
        // timeout while nb accumulated every line, which is the reported failure in
        // miniature; now it stops as soon as the output can no longer be reported.
        var tool = Tool(ceiling: 512 * 1024);
        var result = await Run(tool, "yes hello");

        Assert.Contains("[Output limit reached at", result.Stdout);
        Assert.Contains("process killed", result.Stdout);
        Assert.True(result.Truncated);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task CeilingIsSharedAcrossStdoutAndStderr()
    {
        // The budget is a property of the call, not of the pipe: a command splitting its
        // runaway across both streams must not get two ceilings' worth.
        var tool = Tool(ceiling: 256 * 1024);
        var result = await Run(tool, "yes hello & yes world 1>&2; wait");

        Assert.Contains("[Output limit reached at", result.Stdout);
    }

    [Fact]
    public async Task OutputUnderTheCeiling_ReportsNoLimit()
    {
        var result = await Run(Tool(ceiling: 8L * 1024 * 1024), "seq 1 1000");
        Assert.DoesNotContain("Output limit reached", result.Stdout);
        Assert.True(result.Truncated); // by the sandwich, not by the ceiling
    }
}
