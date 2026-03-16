using System.Runtime.InteropServices;
using nb.Shell;

namespace nb.Tests;

public class TrustSandboxTests
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly string Sep = Path.DirectorySeparatorChar.ToString();

    // Use platform-appropriate paths for testing
    private string Cwd => IsWindows ? @"C:\Projects\myapp" : "/home/user/projects/myapp";
    private string CwdChild(string relative) => Path.Combine(Cwd, relative);

    [Fact]
    public void IsPathTrusted_ExactCwd_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrusted(Cwd, Cwd));
    }

    [Fact]
    public void IsPathTrusted_ChildOfCwd_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrusted(CwdChild("src/file.cs"), Cwd));
    }

    [Fact]
    public void IsPathTrusted_DeeplyNestedChild_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrusted(CwdChild("src/components/ui/Button.tsx"), Cwd));
    }

    [Fact]
    public void IsPathTrusted_ParentOfCwd_ReturnsFalse()
    {
        var parent = Path.GetDirectoryName(Cwd)!;
        Assert.False(TrustSandbox.IsPathTrusted(parent, Cwd));
    }

    [Fact]
    public void IsPathTrusted_SiblingDirectory_ReturnsFalse()
    {
        var sibling = IsWindows ? @"C:\Projects\otherapp" : "/home/user/projects/otherapp";
        Assert.False(TrustSandbox.IsPathTrusted(sibling, Cwd));
    }

    [Fact]
    public void IsPathTrusted_SiblingWithSamePrefix_ReturnsFalse()
    {
        // "myapp-backup" starts with "myapp" but is not a subdirectory
        var sibling = IsWindows ? @"C:\Projects\myapp-backup\file.txt" : "/home/user/projects/myapp-backup/file.txt";
        Assert.False(TrustSandbox.IsPathTrusted(sibling, Cwd));
    }

    [Fact]
    public void IsPathTrusted_RootPath_ReturnsFalse()
    {
        var root = IsWindows ? @"C:\" : "/";
        Assert.False(TrustSandbox.IsPathTrusted(root, Cwd));
    }

    [Fact]
    public void IsPathTrusted_SystemPath_ReturnsFalse()
    {
        var systemPath = IsWindows ? @"C:\Windows\System32\config" : "/etc/passwd";
        Assert.False(TrustSandbox.IsPathTrusted(systemPath, Cwd));
    }

    [Fact]
    public void IsPathTrusted_HomeDirectory_ReturnsFalse()
    {
        var home = IsWindows ? @"C:\Users\user" : "/home/user";
        Assert.False(TrustSandbox.IsPathTrusted(home, Cwd));
    }

    [Fact]
    public void IsPathTrusted_TempDirectory_ReturnsTrue()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "somefile.txt");
        Assert.True(TrustSandbox.IsPathTrusted(tempFile, Cwd));
    }

    [Fact]
    public void IsPathTrusted_NestedTempDirectory_ReturnsTrue()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "nb-work", "output.json");
        Assert.True(TrustSandbox.IsPathTrusted(tempFile, Cwd));
    }

    // --- IsPathTrustedRelative tests ---

    [Fact]
    public void IsPathTrustedRelative_RelativePath_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrustedRelative("src/file.cs", Cwd));
    }

    [Fact]
    public void IsPathTrustedRelative_CurrentDir_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrustedRelative(".", Cwd));
    }

    [Fact]
    public void IsPathTrustedRelative_JustFilename_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrustedRelative("README.md", Cwd));
    }

    [Fact]
    public void IsPathTrustedRelative_AbsoluteOutsideCwd_ReturnsFalse()
    {
        var outsidePath = IsWindows ? @"C:\Windows\System32\drivers" : "/etc/nginx/nginx.conf";
        Assert.False(TrustSandbox.IsPathTrustedRelative(outsidePath, Cwd));
    }

    [Fact]
    public void IsPathTrustedRelative_AbsoluteInsideCwd_ReturnsTrue()
    {
        Assert.True(TrustSandbox.IsPathTrustedRelative(CwdChild("package.json"), Cwd));
    }

    [Theory]
    [InlineData("../otherapp/secret.txt")]
    [InlineData("../../secret.txt")]
    public void IsPathTrustedRelative_ParentTraversal_ReturnsFalse(string relativePath)
    {
        Assert.False(TrustSandbox.IsPathTrustedRelative(relativePath, Cwd));
    }

    [Fact]
    public void IsPathTrustedRelative_TraversalBackIntoCwd_ReturnsTrue()
    {
        // src/../src/file.cs resolves back into cwd
        Assert.True(TrustSandbox.IsPathTrustedRelative("src/../src/file.cs", Cwd));
    }

    [Fact]
    public void IsPathTrusted_CwdWithTrailingSeparator_Works()
    {
        var cwdWithSlash = Cwd + Path.DirectorySeparatorChar;
        Assert.True(TrustSandbox.IsPathTrusted(CwdChild("file.txt"), cwdWithSlash));
    }

    [Fact]
    public void IsPathTrusted_InvalidPath_ReturnsFalse()
    {
        // Null bytes and other invalid chars should return false, not throw
        Assert.False(TrustSandbox.IsPathTrusted("\0invalid", Cwd));
    }
}
