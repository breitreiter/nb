using System.Runtime.InteropServices;

namespace nb.Shell;

/// <summary>Raised when <c>Sandbox: bwrap</c> is requested on a host that can't honor it.</summary>
public sealed class SandboxUnavailableException : Exception
{
    public SandboxUnavailableException(string message) : base(message) { }
}

/// <summary>
/// The bubblewrap (<c>bwrap</c>) bash sandbox (Phase 5.3,
/// plans/approval-policy-and-sandbox.md). Wraps the bash child in a namespace with
/// the whole fs read-only, the cwd + a fresh /tmp writable, known secret dirs masked
/// to empty tmpfs, and no network by default. Kernel-enforced — the only thing that
/// holds against arbitrary read commands and command substitution. Only the bash
/// child is sandboxed in v1.
/// </summary>
public static class BwrapSandbox
{
    // Secret dirs masked to an empty tmpfs so even a read (or `$(cat …)`) sees nothing.
    private static IEnumerable<string> SecretDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;
        foreach (var rel in new[] { ".ssh", ".aws", ".gnupg", ".config/nb" })
            yield return Path.Combine(home, rel);
    }

    /// <summary>Linux with <c>bwrap</c> on PATH. Probed at policy-resolve time; a
    /// requested-but-unavailable sandbox hard-fails the run.</summary>
    public static bool IsAvailable()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && FindOnPath("bwrap") != null;

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var full = Path.Combine(dir, exe);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>Parse a sandbox mode string: <c>none</c> | <c>bwrap</c> | <c>bwrap-net</c>.</summary>
    public static bool TryParse(string value, out SandboxMode mode, out bool allowNet)
    {
        allowNet = false;
        switch (value.Trim().ToLowerInvariant())
        {
            case "none": mode = SandboxMode.None; return true;
            case "bwrap": mode = SandboxMode.Bwrap; return true;
            case "bwrap-net": mode = SandboxMode.Bwrap; allowNet = true; return true;
            default: mode = SandboxMode.None; return false;
        }
    }

    /// <summary>
    /// The argv (after the <c>bwrap</c> program name) that runs <paramref name="command"/>
    /// through <paramref name="shellPath"/> inside the sandbox. Passed literally via
    /// <c>ProcessStartInfo.ArgumentList</c>, so no shell escaping is applied to the command.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(string shellPath, string command, string cwd, bool allowNet)
    {
        var args = new List<string>
        {
            "--ro-bind", "/", "/",   // whole fs read-only …
            "--dev", "/dev",          // … with a fresh /dev, /proc, and writable /tmp
            "--proc", "/proc",
            "--tmpfs", "/tmp",
        };

        // Mask secret dirs (only those that exist — bwrap can't tmpfs a missing mountpoint).
        foreach (var dir in SecretDirs())
            if (Directory.Exists(dir)) { args.Add("--tmpfs"); args.Add(dir); }

        // The cwd is the one writable window onto the real fs.
        args.Add("--bind"); args.Add(cwd); args.Add(cwd);
        args.Add("--chdir"); args.Add(cwd);

        args.Add("--unshare-all");          // new namespaces, incl. network …
        if (allowNet) args.Add("--share-net"); // … unless the policy opts back in.

        args.Add("--");
        args.Add(shellPath);
        args.Add("-c");
        args.Add(command);
        return args;
    }
}
