using System.Runtime.InteropServices;

namespace nb.Shell;

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

    public static bool IsPathTrusted(string path, string cwd)
    {
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
