using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Transcript;

namespace nb.Harness;

/// <summary>
/// The tool surface a run advertises to the model — nb's own, canonical surface.
///
/// This is the base of the harness-emulation hierarchy (`plans/harness-emulation.md`):
/// costumes that imitate another agent's harness (Claude Code, Codex, Cursor,
/// qwen-code) derive from this and override the parts they change, so each costume
/// reads as a diff against a legible default rather than a from-scratch declaration.
/// The default is a real object, not a null check.
///
/// It owns *what is advertised*. It does not own approval, the trust sandbox, the
/// read-tracker or budgets — those keep working in canonical tool identities.
/// MCP tools are not part of a harness either: they are user configuration, not
/// part of the costume.
/// </summary>
public class NbHarness
{
    private readonly TodoManager _todos = new();
    private readonly TodoTool _todoTool;

    public NbHarness(
        BashTool? bash = null,
        ReadFileTool? readFile = null,
        WriteFileTool? writeFile = null,
        EditFileTool? editFile = null,
        FindFilesTool? findFiles = null,
        GrepTool? grep = null,
        ListDirTool? listDir = null,
        FetchUrlTool? fetchUrl = null,
        SearchWebTool? searchWeb = null,
        ApplyPatchTool? applyPatch = null)
    {
        Bash = bash;
        ReadFile = readFile;
        WriteFile = writeFile;
        EditFile = editFile;
        FindFiles = findFiles;
        Grep = grep;
        ListDir = listDir;
        FetchUrl = fetchUrl;
        SearchWeb = searchWeb;
        ApplyPatch = applyPatch;
        _todoTool = new TodoTool(_todos);
    }

    // Null when not wired: --nobash nulls the lot, and EditToolStyle picks
    // ApplyPatch XOR WriteFile+EditFile.
    public BashTool? Bash { get; }
    public ReadFileTool? ReadFile { get; }
    public WriteFileTool? WriteFile { get; }
    public EditFileTool? EditFile { get; }
    public FindFilesTool? FindFiles { get; }
    public GrepTool? Grep { get; }
    public ListDirTool? ListDir { get; }
    public FetchUrlTool? FetchUrl { get; }
    public SearchWebTool? SearchWeb { get; }
    public ApplyPatchTool? ApplyPatch { get; }

    /// <summary>Todo state is per-run and rides the tool surface, so the harness owns it.</summary>
    public TodoManager Todos => _todos;
    public TodoTool Todo => _todoTool;

    /// <summary>The registry name this harness answers to.</summary>
    public virtual string Name => HarnessRegistry.Default;

    /// <summary>
    /// What this costume knowingly does not reproduce. Surfaced with the run so a
    /// surprising behavioural diff against the real harness arrives with a suspect list
    /// attached — silent approximation is what turns a bounded placebo into an
    /// unfalsifiable one. nb's own surface omits nothing, being the real thing.
    /// </summary>
    public virtual IReadOnlyList<string> Omissions => Array.Empty<string>();

    /// <summary>
    /// Translate a call as the model made it into nb's canonical vocabulary, so every
    /// dispatch arm, the approval policy, the trust sandbox and the read-tracker keep
    /// working in canonical tool identities and never learn that costumes exist.
    /// Identity for nb's own surface.
    /// </summary>
    public virtual (string Name, IDictionary<string, object?>? Arguments) ToCanonical(
        string wireName, IDictionary<string, object?>? arguments) => (wireName, arguments);

    /// <summary>
    /// The inverse, for the transcript: a tool round is recorded under the name that
    /// actually went on the wire, so the transcript is a faithful record of what the
    /// model was offered and what it called. Unknown names pass through (MCP and fake
    /// tools are not part of a costume).
    /// </summary>
    public virtual string ToWireName(string canonicalName) => canonicalName;

    /// <summary>
    /// Assemble the native tools this harness advertises, filtered by the surface a
    /// <c>tools</c> directive folded. Order is meaningful — it is the order the model
    /// sees — and is pinned by <c>ToolSurfaceGoldenTests</c>.
    /// </summary>
    public virtual IReadOnlyList<AIFunction> CreateTools(ToolSurface surface)
    {
        var tools = new List<AIFunction>();

        if (Bash != null && surface.AllowsNative("bash")) tools.Add(Bash.CreateTool());
        if (ReadFile != null && surface.AllowsNative("read_file")) tools.Add(ReadFile.CreateTool());
        if (WriteFile != null && surface.AllowsNative("write_file")) tools.Add(WriteFile.CreateTool());
        if (EditFile != null && surface.AllowsNative("edit_file")) tools.Add(EditFile.CreateTool());
        if (FindFiles != null && surface.AllowsNative("find_files")) tools.Add(FindFiles.CreateTool());
        if (Grep != null && surface.AllowsNative("grep")) tools.Add(Grep.CreateTool());
        if (ListDir != null && surface.AllowsNative("list_dir")) tools.Add(ListDir.CreateTool());
        if (ApplyPatch != null && surface.AllowsNative("apply_patch")) tools.Add(ApplyPatch.CreateTool());
        if (FetchUrl != null && surface.AllowsNative("fetch_url")) tools.Add(FetchUrl.CreateTool());
        if (SearchWeb != null && surface.AllowsNative("search_web")) tools.Add(SearchWeb.CreateTool());

        // todo rides the native surface: on by default, dropped by `tools -todo` /
        // `tools none`. Removing it also silences the pending-todos nudge, since no
        // todos can be created without the write tool.
        if (surface.AllowsNative("todo"))
        {
            tools.Add(_todoTool.CreateWriteTool());
            tools.Add(_todoTool.CreateReadTool());
        }

        return tools;
    }
}
