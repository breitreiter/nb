using System.Runtime.InteropServices;

namespace nb.Shell;

// Shape inherited from nb's coding-agent era: keep an agent out of the human's home
// directory while they watch. It is a convenience default, NOT a boundary — a path check
// cannot bound reads, because the command that reads the file need not name it in a form
// this sees (bugs/shell-tool-no-filesystem-sandbox.md, closed as accepted by design).
// Right for the REPL, where the watching human exists. Wrong inside a container, where it
// only yields false denials on legitimate work — see plans/approval-is-not-a-boundary.md.

public static class TrustSandbox
{
    /// <summary>
    /// Check if a path resolves to a location within cwd (or temp dirs).
    /// Also detects symlinks that escape the sandbox.
    /// Returns (trusted, symlinkEscape) — symlinkEscape is true if the
    /// logical path is inside cwd but the real path resolves outside it.
    /// </summary>
    public static (bool Trusted, bool SymlinkEscape) CheckPath(string path, string cwd)
    {
        if (path.Equals(NullSink, StringComparison.Ordinal))
            return (true, false);

        try
        {
            var logical = Path.GetFullPath(path);
            var resolvedCwd = Path.GetFullPath(cwd);
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            var logicallyInside = IsUnderDirectory(logical, resolvedCwd, comparison)
                || IsUnderTempDirectory(logical, comparison);

            // Resolve symlinks to get the real path
            string real;
            try
            {
                var info = new FileInfo(logical);
                real = info.LinkTarget != null
                    ? Path.GetFullPath(info.LinkTarget, Path.GetDirectoryName(logical)!)
                    : logical;
            }
            catch
            {
                real = logical;
            }

            if (logicallyInside && real != logical)
            {
                var reallyInside = IsUnderDirectory(real, resolvedCwd, comparison)
                    || IsUnderTempDirectory(real, comparison);
                if (!reallyInside)
                    return (false, true); // symlink escapes sandbox
            }

            return (logicallyInside, false);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>
    /// The null sink. `CommandClassifier` reports a redirect target as the command's
    /// path, so `find . -name x 2>/dev/null` classifies as a *write to /dev/null* — and
    /// the trust rung then refused it for sitting outside cwd, which denied any command
    /// carrying a `2>/dev/null` (bugs/Trust_Rung_Denies_A_Bare_Find_With_A_Redirect.md).
    /// Discarding output is not an escape from anything, so the sink is trusted.
    /// Deliberately just this one path: a redirect to a *real* path outside cwd still
    /// refuses, because classifying that as a write is arguably right.
    /// </summary>
    private const string NullSink = "/dev/null";

    public static bool IsPathTrusted(string path, string cwd)
    {
        if (path.Equals(NullSink, StringComparison.Ordinal))
            return true;

        try
        {
            var resolved = Path.GetFullPath(path);
            var resolvedCwd = Path.GetFullPath(cwd);

            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return IsUnderDirectory(resolved, resolvedCwd, comparison)
                || IsUnderTempDirectory(resolved, comparison);
        }
        catch
        {
            return false; // If we can't resolve the path, don't trust it
        }
    }

    public static bool IsPathTrustedRelative(string path, string cwd)
    {
        var fullPath = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(cwd, path));
        return IsPathTrusted(fullPath, cwd);
    }

    private static bool IsUnderTempDirectory(string path, StringComparison comparison)
    {
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (IsUnderDirectory(path, tempPath, comparison))
            return true;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var envVar in new[] { "TEMP", "TMP" })
            {
                var envPath = Environment.GetEnvironmentVariable(envVar);
                if (!string.IsNullOrEmpty(envPath))
                {
                    var fullEnvPath = Path.GetFullPath(envPath);
                    if (IsUnderDirectory(path, fullEnvPath, comparison))
                        return true;
                }
            }
        }

        return false;
    }

    private static bool IsUnderDirectory(string path, string directory, StringComparison comparison)
    {
        // Normalize trailing separators
        if (!directory.EndsWith(Path.DirectorySeparatorChar))
            directory += Path.DirectorySeparatorChar;

        return path.StartsWith(directory, comparison) ||
               path.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }
}
