namespace nb.Shell.ApplyPatch;

public sealed class PatchApplyException : Exception
{
    public PatchApplyException(string message) : base(message) { }
}

public enum FileOpKind { Add, Update, UpdateAndMove, Delete }

public sealed record FilePreview(
    string OriginalPath,
    string FinalPath,
    FileOpKind Kind,
    int OldLineCount,
    int NewLineCount,
    string? ComputedContent);

public sealed record PatchPreview(List<FilePreview> Files);

/// <summary>
/// Turns parsed patch operations into a preview (for approval UX) and applies them to disk.
///
/// Flow:
///   1. BuildPreview — validates guards, computes replacements, no writes.
///   2. Apply — writes to disk, applies moves/deletes, updates the read tracker.
/// Splitting lets the caller show a diff and get user approval between the two phases.
/// </summary>
public static class PatchApplier
{
    public static PatchPreview BuildPreview(
        List<FileOp> ops,
        string cwd,
        FileReadTracker tracker)
    {
        var files = new List<FilePreview>();

        foreach (var op in ops)
        {
            var fullPath = Resolve(op.Path, cwd);

            switch (op)
            {
                case AddFile add:
                    if (File.Exists(fullPath))
                        throw new PatchApplyException($"Add File failed: '{op.Path}' already exists");
                    files.Add(new FilePreview(op.Path, fullPath, FileOpKind.Add, 0, CountLines(add.Content), add.Content));
                    break;

                case DeleteFile:
                    if (!File.Exists(fullPath))
                        throw new PatchApplyException($"Delete File failed: '{op.Path}' does not exist");
                    files.Add(new FilePreview(op.Path, fullPath, FileOpKind.Delete, CountLines(File.ReadAllText(fullPath)), 0, null));
                    break;

                case UpdateFile upd:
                    if (!File.Exists(fullPath))
                        throw new PatchApplyException($"Update File failed: '{op.Path}' does not exist");

                    if (!tracker.HasBeenRead(fullPath))
                        throw new PatchApplyException($"Update File failed: '{op.Path}' must be read before editing");
                    if (tracker.HasBeenModifiedSinceRead(fullPath))
                        throw new PatchApplyException($"Update File failed: '{op.Path}' has been modified since it was last read");

                    var original = File.ReadAllText(fullPath);
                    var (updated, oldLc, newLc) = ApplyUpdate(op.Path, original, upd.Chunks);

                    var finalPath = upd.MoveTo != null ? Resolve(upd.MoveTo, cwd) : fullPath;
                    var kind = upd.MoveTo != null ? FileOpKind.UpdateAndMove : FileOpKind.Update;
                    files.Add(new FilePreview(op.Path, finalPath, kind, oldLc, newLc, updated));
                    break;
            }
        }

        return new PatchPreview(files);
    }

    public static void Apply(PatchPreview preview, List<FileOp> ops, string cwd, FileReadTracker tracker)
    {
        for (int i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            var fp = preview.Files[i];

            switch (op)
            {
                case AddFile:
                {
                    var dir = Path.GetDirectoryName(fp.FinalPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(fp.FinalPath, fp.ComputedContent ?? "");
                    tracker.RecordWrite(fp.FinalPath);
                    break;
                }
                case DeleteFile:
                    File.Delete(fp.FinalPath);
                    break;
                case UpdateFile upd:
                {
                    var originalFull = Resolve(op.Path, cwd);
                    File.WriteAllText(originalFull, fp.ComputedContent ?? "");
                    tracker.RecordWrite(originalFull);
                    if (upd.MoveTo != null)
                    {
                        var dir = Path.GetDirectoryName(fp.FinalPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        File.Move(originalFull, fp.FinalPath, overwrite: false);
                        tracker.RecordWrite(fp.FinalPath);
                    }
                    break;
                }
            }
        }
    }

    private static (string Content, int OldLineCount, int NewLineCount) ApplyUpdate(
        string relPath, string original, List<UpdateChunk> chunks)
    {
        var fileUsesCrlf = original.Contains("\r\n");
        var normalized = fileUsesCrlf ? original.Replace("\r\n", "\n") : original;

        // Preserve the final-newline state so we can restore it.
        var endsWithNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1] == "")
            lines.RemoveAt(lines.Count - 1);

        var cursor = 0;
        var replacements = new List<(int Index, int OldLen, List<string> NewLines)>();

        foreach (var chunk in chunks)
        {
            foreach (var header in chunk.ContextHeaders)
            {
                var idx = SeekSequence.Find(lines, new[] { header }, cursor);
                if (idx < 0)
                    throw new PatchApplyException($"Update File '{relPath}': failed to find context header '@@ {Truncate(header)}'");
                cursor = idx + 1;
            }

            int matchIndex;
            if (chunk.OldLines.Count == 0)
            {
                // Pure insertion — codex inserts at EOF.
                matchIndex = lines.Count;
            }
            else
            {
                if (chunk.IsEndOfFile)
                {
                    var tailStart = Math.Max(cursor, lines.Count - chunk.OldLines.Count);
                    matchIndex = SeekSequence.Find(lines, chunk.OldLines, tailStart);
                    if (matchIndex < 0)
                        matchIndex = SeekSequence.Find(lines, chunk.OldLines, cursor);
                }
                else
                {
                    matchIndex = SeekSequence.Find(lines, chunk.OldLines, cursor);
                }

                // Retry without trailing empty line (phantom from split).
                if (matchIndex < 0 && chunk.OldLines.Count > 0 && chunk.OldLines[^1] == "")
                {
                    var trimmed = chunk.OldLines.Take(chunk.OldLines.Count - 1).ToList();
                    matchIndex = SeekSequence.Find(lines, trimmed, cursor);
                }

                if (matchIndex < 0)
                {
                    var preview = string.Join("\n", chunk.OldLines.Take(3));
                    throw new PatchApplyException(
                        $"Update File '{relPath}': failed to locate chunk. Expected lines not found:\n{preview}");
                }
            }

            replacements.Add((matchIndex, chunk.OldLines.Count, chunk.NewLines));
            cursor = matchIndex + chunk.OldLines.Count;
        }

        replacements.Sort((a, b) => a.Index.CompareTo(b.Index));

        var result = new List<string>(lines.Count + 32);
        var pos = 0;
        var oldLineCount = 0;
        var newLineCount = 0;
        foreach (var (idx, oldLen, newLines) in replacements)
        {
            while (pos < idx) result.Add(lines[pos++]);
            result.AddRange(newLines);
            pos = idx + oldLen;
            oldLineCount += oldLen;
            newLineCount += newLines.Count;
        }
        while (pos < lines.Count) result.Add(lines[pos++]);

        var sep = fileUsesCrlf ? "\r\n" : "\n";
        var content = string.Join(sep, result);
        if (endsWithNewline) content += sep;

        return (content, oldLineCount, newLineCount);
    }

    private static string Resolve(string path, string cwd) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(cwd, path));

    private static int CountLines(string s) => s.Length == 0 ? 0 : s.Split('\n').Length;

    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
