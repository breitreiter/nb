using System.Runtime.InteropServices;

namespace nb.Shell;

public static class TrustSandbox
{
    public static bool IsPathTrusted(string path, string cwd)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            var resolvedCwd = Path.GetFullPath(cwd);

            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            // Check if path is within cwd
            if (IsUnderDirectory(resolved, resolvedCwd, comparison))
                return true;

            // Check if path is within temp directories
            var tempPath = Path.GetFullPath(Path.GetTempPath());
            if (IsUnderDirectory(resolved, tempPath, comparison))
                return true;

            // On Windows, also check TEMP/TMP env vars explicitly
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                foreach (var envVar in new[] { "TEMP", "TMP" })
                {
                    var envPath = Environment.GetEnvironmentVariable(envVar);
                    if (!string.IsNullOrEmpty(envPath))
                    {
                        var fullEnvPath = Path.GetFullPath(envPath);
                        if (IsUnderDirectory(resolved, fullEnvPath, comparison))
                            return true;
                    }
                }
            }

            return false;
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

    private static bool IsUnderDirectory(string path, string directory, StringComparison comparison)
    {
        // Normalize trailing separators
        if (!directory.EndsWith(Path.DirectorySeparatorChar))
            directory += Path.DirectorySeparatorChar;

        return path.StartsWith(directory, comparison) ||
               path.Equals(directory.TrimEnd(Path.DirectorySeparatorChar), comparison);
    }
}
