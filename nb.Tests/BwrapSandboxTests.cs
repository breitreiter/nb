using nb.Shell;

namespace nb.Tests;

public class BwrapSandboxTests
{
    [Theory]
    [InlineData("none", SandboxMode.None, false)]
    [InlineData("bwrap", SandboxMode.Bwrap, false)]
    [InlineData("bwrap-net", SandboxMode.Bwrap, true)]
    [InlineData("BWRAP", SandboxMode.Bwrap, false)]         // case-insensitive
    [InlineData("  bwrap-net  ", SandboxMode.Bwrap, true)]  // trimmed
    public void TryParse_KnownModes(string value, SandboxMode expected, bool expectedNet)
    {
        Assert.True(BwrapSandbox.TryParse(value, out var mode, out var net));
        Assert.Equal(expected, mode);
        Assert.Equal(expectedNet, net);
    }

    [Theory]
    [InlineData("")]
    [InlineData("firejail")]
    [InlineData("bwrapnet")]
    public void TryParse_UnknownMode_FailsAndDefaultsToNone(string value)
    {
        Assert.False(BwrapSandbox.TryParse(value, out var mode, out var net));
        Assert.Equal(SandboxMode.None, mode);
        Assert.False(net);
    }

    private static IReadOnlyList<string> Args(bool net = false) =>
        BwrapSandbox.BuildArgs("/bin/bash", "echo hi", "/home/u/proj", net);

    [Fact]
    public void BuildArgs_WholeFsReadOnly_CwdAndTmpWritable()
    {
        var a = Args();
        AssertAdjacent(a, "--ro-bind", "/", "/");                  // whole fs read-only …
        AssertAdjacent(a, "--tmpfs", "/tmp");                       // … with a writable /tmp …
        AssertAdjacent(a, "--bind", "/home/u/proj", "/home/u/proj"); // … and the cwd as the one writable window
        AssertAdjacent(a, "--chdir", "/home/u/proj");
    }

    [Fact]
    public void BuildArgs_UnshareAll_NoNetByDefault()
    {
        var a = Args(net: false);
        Assert.Contains("--unshare-all", a);
        Assert.DoesNotContain("--share-net", a);
    }

    [Fact]
    public void BuildArgs_NetOptsBackIn()
    {
        var a = Args(net: true);
        Assert.Contains("--unshare-all", a);
        Assert.Contains("--share-net", a);
    }

    [Fact]
    public void BuildArgs_CommandPassedLiterally_AfterSeparator()
    {
        // The -c payload is a single argv entry, NOT shell-escaped — this is what keeps
        // ArgumentList injection-safe: the sandbox flags and the command never share a token.
        var cmd = "echo \"$HOME\"; rm -rf /nope && cat 'a b'";
        var a = BwrapSandbox.BuildArgs("/bin/bash", cmd, "/w", allowNet: false);
        var sep = a.ToList().IndexOf("--");
        Assert.True(sep >= 0, "expected a -- separator before the shell invocation");
        Assert.Equal(new[] { "/bin/bash", "-c", cmd }, a.Skip(sep + 1).ToArray());
    }

    // Assert that `seq` appears as consecutive elements somewhere in `args`.
    private static void AssertAdjacent(IReadOnlyList<string> args, params string[] seq)
    {
        var list = args.ToList();
        for (int i = 0; i + seq.Length <= list.Count; i++)
            if (list.Skip(i).Take(seq.Length).SequenceEqual(seq)) return;
        Assert.Fail($"expected consecutive [{string.Join(' ', seq)}] within [{string.Join(' ', list)}]");
    }
}
