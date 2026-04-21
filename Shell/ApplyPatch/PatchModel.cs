namespace nb.Shell.ApplyPatch;

public abstract record FileOp(string Path);
public sealed record AddFile(string Path, string Content) : FileOp(Path);
public sealed record DeleteFile(string Path) : FileOp(Path);
public sealed record UpdateFile(string Path, string? MoveTo, List<UpdateChunk> Chunks) : FileOp(Path);

/// <summary>
/// A single contiguous change block within an UpdateFile.
/// ContextHeaders are the `@@`-prefixed anchors that must be located first, in order,
/// advancing a forward-only cursor. OldLines is what currently exists (context + removals),
/// NewLines is what replaces it (context + additions).
/// </summary>
public sealed record UpdateChunk(
    List<string> ContextHeaders,
    List<string> OldLines,
    List<string> NewLines,
    bool IsEndOfFile);

public sealed class PatchParseException : Exception
{
    public int LineNumber { get; }
    public PatchParseException(string message, int lineNumber) : base($"line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }
}
