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
                @@ optional_context_anchor
                 context line (unchanged)
                -line to remove
                +line to add
                *** Add File: path/to/new
                +every line of the new file
                *** Delete File: path/to/old
                *** End Patch

                Rules:
                - Paths are relative to: {{_env.ShellCwd}}
                - Files being updated must have been read first (use read_file)
                - Each change line starts with exactly one sigil: ' ' (context), '-' (remove), '+' (add) — the character immediately after the sigil is content
                - `@@ header` lines anchor a chunk's location; use multiple headers to disambiguate nested scopes (e.g. @@ class Foo then @@ def bar)
                - Within one Update File, chunks are located in document order via a forward-only cursor — duplicate code must be disambiguated with @@ headers
                - `*** End of File` after a chunk means "this edit is at EOF"
                - Rename via `*** Move to: new/path` placed immediately after `*** Update File:`

                On success returns a summary. On failure returns a parse or apply error and the patch is NOT partially applied.
                """);
    }
}
