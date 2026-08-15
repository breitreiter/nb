using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Transcript;

namespace nb.Harness;

/// <summary>
/// The qwen-code surface — the harness Qwen3-Coder was post-trained against.
///
/// This is the costume with evidence behind it:
/// <c>bugs/Tool_Names_Diverge_From_Model_Native_Surface.md</c> measured a run where the
/// model reached for <c>write_file</c> (a name it recognised) 6 times and <c>edit_file</c>
/// (a name it did not) once, rewriting whole files instead of editing them until the run
/// aborted on budget. Names and parameter spellings are verified against
/// <c>QwenLM/qwen-code</c> (Apache-2.0) at <c>packages/core/src/tools/</c>.
///
/// The renaming is confined to what this costume advertises and how <c>DispatchAsync</c>
/// unpacks it; everything downstream keeps working in nb's canonical names.
/// </summary>
public sealed class QwenCodeHarness : NbHarness
{
    public const string HarnessName = "qwen-code";

    public QwenCodeHarness(
        BashTool? bash = null, ReadFileTool? readFile = null, WriteFileTool? writeFile = null,
        EditFileTool? editFile = null, FindFilesTool? findFiles = null, GrepTool? grep = null,
        ListDirTool? listDir = null, FetchUrlTool? fetchUrl = null, SearchWebTool? searchWeb = null,
        ApplyPatchTool? applyPatch = null)
        : base(bash, readFile, writeFile, editFile, findFiles, grep, listDir, fetchUrl, searchWeb, applyPatch)
    {
    }

    /// <summary>Wear QwenCodeHarness's surface over an existing harness's tools.</summary>
    public QwenCodeHarness(NbHarness source) : base(source) { }

    public override string Name => HarnessName;

    /// <summary>
    /// nb-authored prose occupying the same channel as qwen-code's system prompt. Read
    /// once per process — a preamble is a data file, but it is not expected to change
    /// under a running program.
    /// </summary>
    public override string? Preamble => _preamble;

    private static readonly string? _preamble = LoadPreamble("qwen-code.md");

    public override IReadOnlyList<string> Omissions
    {
        get
        {
            var omissions = new List<string>();

            // The preamble is a deployed data file, so its absence is a real runtime
            // state and not a hypothetical. Say which one is true rather than claiming a
            // prompt that may not have loaded.
            omissions.Add(_preamble is null
                ? "system prompt: MISSING — prompts/harness/qwen-code.md did not load, so this run is tool-surface-only."
                : "system prompt: an nb-authored facsimile, not qwen-code's text. It occupies the same channel (role, conventions-first mandates, plan/implement/verify workflow, terse CLI output contract, the edit-over-rewrite steer) in original prose. Wording differences from the real prompt are unmeasured.");

            omissions.AddRange(_omissions);
            return omissions;
        }
    }

    private static readonly string[] _omissions = new[]
    {
        "tool descriptions: nb's own prose, with corrected parameter names. qwen-code's descriptions are Apache-2.0 and vendorable, and are not vendored.",
        "environment block: none. qwen-code injects one; its shape was not researched, and an invented block is worse than an absent one because the model reads it as fact. The consequence is concrete — expect the model to open by running pwd, since nothing has told it where it is.",
        "result formatting: nb's own strings, unchanged — the exit-code footer on run_shell_command, the edit and write acknowledgments. The codex and claude-code costumes reshape theirs; this one does not, because qwen-code's exact result text was not researched and guessing at it would be worse than saying so.",
        "run_shell_command: is_background, directory and timeout are all accepted and ignored — nb's bash runs foreground in the shell cwd, under its own configured timeout. qwen-code's timeout is in milliseconds and nb's is in seconds, but nothing converts between them because the shared bash capability takes no timeout at all; see bugs/Bash_Advertises_A_Timeout_It_Ignores.md.",
        "web_fetch: qwen-code runs a model over the fetched page and answers the prompt; nb's fetch_url returns the content. The prompt and format arguments are accepted and ignored.",
        "list_directory: ignore and file_filtering_options are not offered.",
        "surface size: qwen-code advertises ~46 tools (agent, skill, plan mode, cron, sub-sessions, …). This costume covers the file/shell/search core only.",
        "todo_read: not offered — qwen-code declares todo_write with no read counterpart.",
    };

    public override IReadOnlyList<AIFunction> CreateTools(ToolSurface surface)
    {
        var tools = new List<AIFunction>();

        if (Bash != null && surface.AllowsNative("bash"))
            tools.Add(Declare("run_shell_command", Bash.CreateTool().Description, new SchemaBuilder()
                .Add("command", "string", "The shell command to execute.", required: true)
                .Add("is_background", "boolean", "Whether to run the command in the background.", required: true)
                .Add("description", "string", "Brief explanation of what this command does and why.")
                .Add("timeout", "number", "Timeout in milliseconds for foreground commands.")
                .Add("directory", "string", "Working directory for command execution.")));

        if (ReadFile != null && surface.AllowsNative("read_file"))
            tools.Add(Declare("read_file", ReadFile.CreateTool().Description, new SchemaBuilder()
                .Add("file_path", "string", "The path to the file to read.", required: true)
                .Add("offset", "number", "1-based line number to start from.")
                .Add("limit", "number", "Maximum number of lines to return.")));

        if (WriteFile != null && surface.AllowsNative("write_file"))
            tools.Add(Declare("write_file", WriteFile.CreateTool().Description, new SchemaBuilder()
                .Add("file_path", "string", "The path to the file to write.", required: true)
                .Add("content", "string", "The content to write to the file.", required: true)));

        if (EditFile != null && surface.AllowsNative("edit_file"))
            tools.Add(Declare("edit", EditFile.CreateTool().Description, new SchemaBuilder()
                .Add("file_path", "string", "The path to the file to modify.", required: true)
                .Add("old_string", "string", "The exact text to replace.", required: true)
                .Add("new_string", "string", "The text to replace it with.", required: true)
                .Add("replace_all", "boolean", "Replace every occurrence instead of requiring a unique match.")));

        if (FindFiles != null && surface.AllowsNative("find_files"))
            tools.Add(Declare("glob", FindFiles.CreateTool().Description, new SchemaBuilder()
                .Add("pattern", "string", "The glob pattern to match against.", required: true)
                .Add("path", "string", "The directory to search within.")));

        if (Grep != null && surface.AllowsNative("grep"))
            tools.Add(Declare("grep_search", Grep.CreateTool().Description, new SchemaBuilder()
                .Add("pattern", "string", "The regular expression to search for.", required: true)
                .Add("path", "string", "The directory to search within.")
                .Add("glob", "string", "Glob limiting which files are searched.")
                .Add("limit", "number", "Maximum number of matches to return.")));

        if (ListDir != null && surface.AllowsNative("list_dir"))
            tools.Add(Declare("list_directory", ListDir.CreateTool().Description, new SchemaBuilder()
                .Add("path", "string", "The directory to list.", required: true)));

        // apply_patch is deliberately NOT advertised: qwen-code has no such tool, and a
        // costume that adds tools its target does not have is as unfaithful as one that
        // drops tools it does have. The goal is a model that behaves like it is in
        // qwen-code, not a model with a larger toolbox.
        //
        // It used to be worse than a judgement call: EditToolStyle: ApplyPatch made the
        // runtime build apply_patch *instead of* write_file + edit_file, so this costume
        // was left with no edit tool at all and had to report the conflict. The runtime
        // now builds every edit tool and lets the harness choose, so the costume always
        // has the two it wants.

        if (FetchUrl != null && surface.AllowsNative("fetch_url"))
            tools.Add(Declare("web_fetch", FetchUrl.CreateTool().Description, new SchemaBuilder()
                .Add("url", "string", "The URL to fetch content from.", required: true)
                .Add("prompt", "string", "The prompt to run on the fetched content.", required: true)
                .Add("format", "string", "Preferred content format.")));

        if (SearchWeb != null && surface.AllowsNative("search_web"))
            tools.Add(Declare("web_search", SearchWeb.CreateTool().Description, new SchemaBuilder()
                .Add("query", "string", "The search query.", required: true)));

        // qwen-code declares todo_write with no read counterpart, and nb's write tool
        // already carries that name.
        if (surface.AllowsNative("todo"))
            tools.Add(Todo.CreateWriteTool());

        return tools;
    }

    private static AIFunction Declare(string name, string? description, SchemaBuilder schema) =>
        new DeclaredFunction(name, description ?? "", schema.Build());

    /// <summary>
    /// qwen-code's argument spellings, unpacked onto the shared capabilities.
    ///
    /// This replaces what used to be three translation tables and a generic argument
    /// rewriter. The adaptation is now ordinary typed code in the costume that owns it:
    /// file_path becomes a path argument by being passed as one, rather than a special
    /// case buried in a rename loop.
    /// </summary>
    protected override async Task<ToolOutcome?> DispatchAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "run_shell_command" when Bash != null:
                // is_background, directory and timeout are accepted and ignored; nb's bash
                // runs foreground in the shell cwd under its own configured timeout.
                return await HandleBashToolCall(callId, Str(arguments, "command"), Str(arguments, "description"));

            case "read_file" when ReadFile != null:
                return HandleReadFileToolCall(callId, Str(arguments, "file_path"),
                    Int(arguments, "offset"), Int(arguments, "limit"));

            case "write_file" when WriteFile != null:
                return await HandleWriteFileToolCall(callId, Str(arguments, "file_path"), Str(arguments, "content"));

            case "edit" when EditFile != null:
                return HandleEditFileToolCall(callId, Str(arguments, "file_path"), Str(arguments, "old_string"),
                    Str(arguments, "new_string"), Bool(arguments, "replace_all") ?? false);

            case "glob" when FindFiles != null:
                return HandleFindFilesToolCall(callId, Str(arguments, "pattern"), StrOrNull(arguments, "path"), null);

            case "grep_search" when Grep != null:
                return HandleGrepToolCall(callId, Str(arguments, "pattern"), StrOrNull(arguments, "path"),
                    StrOrNull(arguments, "glob"), null, Int(arguments, "limit"), null);

            case "list_directory" when ListDir != null:
                return HandleListDirToolCall(callId, StrOrNull(arguments, "path"));

            case "web_fetch" when FetchUrl != null:
                // qwen-code answers a prompt against the page; nb returns the content.
                // The prompt and format arguments are accepted and ignored.
                return await HandleFetchUrlToolCall(callId, Str(arguments, "url"));

            case "web_search" when SearchWeb != null:
                return await HandleSearchWebToolCall(callId, Str(arguments, "query"));

            case "todo_write":
                return HandleTodoWriteToolCall(callId, arguments);

            default:
                return null;
        }
    }
}
