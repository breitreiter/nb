using Microsoft.Extensions.AI;
using nb.Shell.ApplyPatch;

namespace nb.Shell;

public class ApplyPatchTool
{
    private readonly ShellEnvironment _env;

    public ApplyPatchTool(ShellEnvironment env)
    {
        _env = env;
    }

    public string GetCwd() => _env.ShellCwd;

    public AIFunction CreateTool()
    {
        var fn = (string input) => input;

        return AIFunctionFactory.Create(
            fn,
            name: "apply_patch",
            description: $$"""
                Applies a multi-file patch. **Preferred for GPT-family models**, which are trained on this format; other models should prefer edit_file and write_file.

                The `input` parameter contains a complete patch in this format:

                *** Begin Patch
                *** Update File: path/to/file
                @@ class Foo
                @@   def bar(self):
                 context line (unchanged)
                -line to remove
                +line to add
                 context line (unchanged)
                *** Add File: path/to/new
                +every line of the new file
                *** Delete File: path/to/old
                *** End Patch

                Rules:
                - Paths are relative to: {{_env.ShellCwd}}
                - Files being updated must have been read first (use read_file)
                - Each change line starts with exactly one sigil: ' ' (context), '-' (remove), '+' (add) — the character immediately after the sigil is content. No line numbers.
                - Include ~3 lines of unchanged context before and after each change so the chunk anchors uniquely — enough to disambiguate, not so much as to be wasteful
                - `@@ header` lines anchor a chunk's location; stack multiple headers to descend into nested scopes (e.g. `@@ class Foo` then `@@   def bar`)
                - Within one Update File, chunks are located in document order via a forward-only cursor — duplicate code must be disambiguated with @@ headers
                - `*** End of File` after a chunk means "this edit is at EOF"
                - Rename via `*** Move to: new/path` placed immediately after `*** Update File:`

                For large changes:
                - Split the edit into many small `@@`-anchored chunks rather than one giant chunk — small chunks locate reliably and are cheaper to emit
                - To replace an entire file, use `*** Delete File:` followed by `*** Add File:` instead of diffing the whole body
                - Never include unchanged regions of the file outside the ~3-line context window of an actual change

                On success returns a summary. On failure returns a parse or apply error and the patch is NOT partially applied.
                """);
    }
}
