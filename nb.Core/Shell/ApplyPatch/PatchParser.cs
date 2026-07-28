namespace nb.Shell.ApplyPatch;

/// <summary>
/// Parses the codex apply_patch sentinel format into a list of file operations.
///
/// Grammar (line-oriented, sentinels column-anchored):
///   *** Begin Patch
///   (*** Add File: PATH \n +LINE... | *** Delete File: PATH | update-block)+
///   *** End Patch
///
/// update-block: *** Update File: PATH \n [*** Move to: PATH \n] chunk*
/// chunk: (@@[ HEADER]? \n)* change-line+ [*** End of File \n]?
/// change-line: ('+' | '-' | ' ') rest-of-line
/// </summary>
public static class PatchParser
{
    private const string BeginPatch = "*** Begin Patch";
    private const string EndPatch = "*** End Patch";
    private const string AddPrefix = "*** Add File: ";
    private const string DeletePrefix = "*** Delete File: ";
    private const string UpdatePrefix = "*** Update File: ";
    private const string MovePrefix = "*** Move to: ";
    private const string EndOfFile = "*** End of File";

    public static List<FileOp> Parse(string input)
    {
        var lines = input.Replace("\r\n", "\n").Split('\n');
        var i = FindBegin(lines);
        if (i < 0)
            throw new PatchParseException("patch must start with '*** Begin Patch'", 1);
        i++;

        var ops = new List<FileOp>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line == EndPatch) return ops;

            if (line.StartsWith(AddPrefix))
            {
                var path = line[AddPrefix.Length..];
                i++;
                ops.Add(ParseAdd(path, lines, ref i));
            }
            else if (line.StartsWith(DeletePrefix))
            {
                var path = line[DeletePrefix.Length..];
                ops.Add(new DeleteFile(path));
                i++;
            }
            else if (line.StartsWith(UpdatePrefix))
            {
                var path = line[UpdatePrefix.Length..];
                i++;
                ops.Add(ParseUpdate(path, lines, ref i));
            }
            else if (string.IsNullOrEmpty(line) && i == lines.Length - 1)
            {
                // Trailing empty line from final newline — tolerate.
                i++;
            }
            else
            {
                throw new PatchParseException($"unexpected line: {Truncate(line)}", i + 1);
            }
        }

        throw new PatchParseException("patch is missing '*** End Patch'", lines.Length);
    }

    private static int FindBegin(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
            if (lines[i] == BeginPatch) return i;
        return -1;
    }

    private static AddFile ParseAdd(string path, string[] lines, ref int i)
    {
        var body = new List<string>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;
            if (line.Length == 0)
            {
                // Blank line inside add-body — treat as empty content line.
                body.Add("");
                i++;
                continue;
            }
            if (line[0] != '+')
                throw new PatchParseException($"Add File body must start with '+': {Truncate(line)}", i + 1);
            body.Add(line[1..]);
            i++;
        }
        return new AddFile(path, string.Join("\n", body));
    }

    private static UpdateFile ParseUpdate(string path, string[] lines, ref int i)
    {
        string? moveTo = null;
        if (i < lines.Length && lines[i].StartsWith(MovePrefix))
        {
            moveTo = lines[i][MovePrefix.Length..];
            i++;
        }

        var chunks = new List<UpdateChunk>();
        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;

            var chunk = ParseChunk(lines, ref i);
            if (chunk != null) chunks.Add(chunk);
        }

        if (chunks.Count == 0)
            throw new PatchParseException($"Update File '{path}' has no chunks", i);

        return new UpdateFile(path, moveTo, chunks);
    }

    private static UpdateChunk? ParseChunk(string[] lines, ref int i)
    {
        var headers = new List<string>();
        while (i < lines.Length && lines[i].StartsWith("@@"))
        {
            // "@@" or "@@ <header>" — capture the header text (may be empty)
            var raw = lines[i];
            var header = raw.Length > 2 && raw[2] == ' ' ? raw[3..] : "";
            if (!string.IsNullOrEmpty(header)) headers.Add(header);
            i++;
        }

        var oldLines = new List<string>();
        var newLines = new List<string>();
        var isEof = false;
        var sawChange = false;

        while (i < lines.Length)
        {
            var line = lines[i];
            if (IsFileHeader(line) || line == EndPatch) break;
            if (line.StartsWith("@@")) break;

            if (line == EndOfFile)
            {
                isEof = true;
                i++;
                break;
            }

            if (line.Length == 0)
            {
                // A truly blank line counts as a context line with empty content.
                oldLines.Add("");
                newLines.Add("");
                sawChange = true;
                i++;
                continue;
            }

            var sigil = line[0];
            var body = line[1..];
            if (sigil == ' ')
            {
                oldLines.Add(body);
                newLines.Add(body);
            }
            else if (sigil == '-')
            {
                oldLines.Add(body);
            }
            else if (sigil == '+')
            {
                newLines.Add(body);
            }
            else
            {
                // Not a valid change line — end of this chunk.
                break;
            }
            sawChange = true;
            i++;
        }

        if (!sawChange && headers.Count == 0) return null;
        return new UpdateChunk(headers, oldLines, newLines, isEof);
    }

    private static bool IsFileHeader(string line) =>
        line.StartsWith(AddPrefix) || line.StartsWith(DeletePrefix) || line.StartsWith(UpdatePrefix);

    private static string Truncate(string s) => s.Length > 80 ? s[..80] + "…" : s;
}
