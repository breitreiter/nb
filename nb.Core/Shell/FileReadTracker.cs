namespace nb.Shell;

/// <summary>
/// Tracks which files have been read and their content hashes at read time.
/// Used to enforce read-before-edit/write and detect external modifications.
/// </summary>
public class FileReadTracker
{
    // path -> content hash at time of last read
    private readonly Dictionary<string, string> _readFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an edit must be preceded by a read through one of nb's file tools.
    ///
    /// True for nb's own surface. A harness costume whose target has no read tool turns
    /// it off — under <c>codex</c> the model reads with <c>sed</c>/<c>cat</c> through the
    /// shell, which this tracker cannot see, so leaving it on would fail every edit the
    /// costume can make. Stale-overwrite detection (<see cref="HasBeenModifiedSinceRead"/>)
    /// is unaffected; it only ever fires on files that *were* read.
    /// </summary>
    public bool RequireReadBeforeEdit { get; set; } = true;

    /// <summary>Record that a file was read. Stores a hash of its current content.</summary>
    public void RecordRead(string fullPath)
    {
        if (!File.Exists(fullPath)) return;
        _readFiles[fullPath] = ComputeHash(fullPath);
    }

    /// <summary>Check if a file has been read in this session.</summary>
    public bool HasBeenRead(string fullPath) =>
        !RequireReadBeforeEdit || _readFiles.ContainsKey(fullPath);

    /// <summary>Check if a file has been modified since it was last read.</summary>
    public bool HasBeenModifiedSinceRead(string fullPath)
    {
        if (!_readFiles.TryGetValue(fullPath, out var readHash))
            return false; // never read = can't compare
        if (!File.Exists(fullPath))
            return true; // deleted since read
        return ComputeHash(fullPath) != readHash;
    }

    /// <summary>Update the stored hash after a successful write/edit.</summary>
    public void RecordWrite(string fullPath)
    {
        if (!File.Exists(fullPath)) return;
        _readFiles[fullPath] = ComputeHash(fullPath);
    }

    /// <summary>Clear all tracking state (e.g. on /clear).</summary>
    public void Clear() => _readFiles.Clear();

    private static string ComputeHash(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
