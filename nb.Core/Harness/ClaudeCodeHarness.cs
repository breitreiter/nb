using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Transcript;

namespace nb.Harness;

/// <summary>
/// The Claude Code surface — the largest of the costumes, and the one where the two
/// halves come from the most different places.
///
/// The <b>surface</b> is reproduced exactly: PascalCase tool names, <c>file_path</c>
/// throughout, Grep's <c>output_mode</c> enum and its ripgrep-shaped flag parameters.
/// Those are interface facts taken from the harness's published tool declarations, and
/// reimplementing an interface to interoperate is the defensible half.
///
/// The <b>prompt</b> is an nb-authored facsimile written from vendor documentation and
/// observed behaviour, because Claude Code's is closed and there is a takedown precedent
/// against reverse-engineering it specifically. See the header of
/// <c>prompts/harness/claude-code.md</c> for the sources that were ruled out and why.
/// </summary>
public sealed class ClaudeCodeHarness : NbHarness
{
    public const string HarnessName = "claude-code";

    /// <summary>Claude Code's budget for a project instruction file.</summary>
    private const int ClaudeMdMaxBytes = 32 * 1024;

    public ClaudeCodeHarness(
        BashTool? bash = null, ReadFileTool? readFile = null, WriteFileTool? writeFile = null,
        EditFileTool? editFile = null, FindFilesTool? findFiles = null, GrepTool? grep = null,
        ListDirTool? listDir = null, FetchUrlTool? fetchUrl = null, SearchWebTool? searchWeb = null,
        ApplyPatchTool? applyPatch = null)
        : base(bash, readFile, writeFile, editFile, findFiles, grep, listDir, fetchUrl, searchWeb, applyPatch)
    {
    }

    public override string Name => HarnessName;

    public override string? Preamble => _preamble;

    private static readonly string? _preamble = LoadPreamble("claude-code.md");

    /// <summary>
    /// CLAUDE.md, in Claude Code's own wrapper. The harness injects project instruction
    /// files inside a <c>&lt;system-reminder&gt;</c> block, which is the convention that
    /// marks injected context as distinct from the user's own words — reproducing the
    /// channel without the wrapper would land the same text in a different register.
    /// </summary>
    public override string? ProjectInstructions()
    {
        var docs = DiscoverProjectDocs(WorkingDirectory, ClaudeMdMaxBytes, "CLAUDE.md");
        if (docs.Count == 0) return null;

        var blocks = docs.Select(d =>
            $"Contents of {d.Path} (project instructions, checked into the codebase):\n\n{d.Text.TrimEnd()}");

        return "<system-reminder>\n"
            + "Codebase and user instructions are shown below. Be sure to adhere to these instructions. "
            + "IMPORTANT: These instructions OVERRIDE any default behavior and you MUST follow them exactly as written.\n\n"
            + string.Join("\n\n", blocks)
            + "\n</system-reminder>";
    }

    public override IReadOnlyList<string> Omissions
    {
        get
        {
            var omissions = new List<string>
            {
                _preamble is null
                    ? "system prompt: MISSING — prompts/harness/claude-code.md did not load, so this run is tool-surface-only."
                    : "system prompt: an nb-authored facsimile, not Claude Code's text, which is closed and was not transcribed from any source — including from an assistant running as Claude Code. It occupies the same channels (terse terminal output contract, conventions-before-invention, the checklist tool, no unsolicited comments, bounded proactiveness) in original prose. Wording differences from the real prompt are unmeasured, and this is the costume where that gap is widest.",
            };
            omissions.AddRange(_omissions);
            return omissions;
        }
    }

    private static readonly string[] _omissions = new[]
    {
        "Task / Skill / NotebookEdit: declared but not backed. They exist and are callable so the model can see the surface it expects, and each returns a note saying it did nothing. A run that depends on one of them is not measuring what it thinks it is. They cannot be filtered individually by a tools directive — they ride the surface as a group and go with `tools none`.",
        "BashOutput / KillShell: not offered, and Bash's run_in_background is accepted and ignored. nb's bash runs foreground, so advertising the poll-and-kill pair would invite the model into a loop it can never finish — worse than not backgrounding at all.",
        "Grep: type, multiline, -n and the -A/-B/-C context flags are accepted and ignored; output_mode `count` falls back to files_with_matches. pattern, path, glob, -i, head_limit and the content/files_with_matches modes are real.",
        "Read: reproduces the line-numbered format and the image branch, but not Claude Code's PDF page ranges or notebook cell rendering.",
        "WebFetch: Claude Code runs a model over the fetched page to answer the prompt; nb's fetch_url returns the content. The prompt argument is accepted and ignored.",
        "WebSearch: allowed_domains and blocked_domains are accepted and ignored.",
        "TodoWrite: activeForm is accepted and ignored — nb's todo list has one label per item, not a present-tense variant.",
        "CLAUDE.md: loaded from the project root down to the cwd inside a <system-reminder> wrapper, but sent as a system message rather than injected mid-conversation the way the real harness re-issues reminders. The user-level file at ~/.claude/CLAUDE.md is not read.",
        "surface size: Claude Code also ships slash commands, hooks, plugins, MCP resource tools, background shells and the agent/skill ecosystem. This costume covers the file/shell/search/web core plus the three declared stubs.",
    };

    public override IReadOnlyList<AIFunction> CreateTools(ToolSurface surface)
    {
        var tools = new List<AIFunction>();

        // Task leads in the real surface, and order is what the model sees.
        if (AnyNativeToolAllowed(surface))
            tools.Add(Declare("Task",
                "Launch a new agent to handle complex, multi-step tasks autonomously.",
                new SchemaBuilder()
                    .Add("description", "string", "A short (3-5 word) description of the task.", required: true)
                    .Add("prompt", "string", "The task for the agent to perform.", required: true)
                    .Add("subagent_type", "string", "The type of specialized agent to use for this task.", required: true)));

        if (Bash != null && surface.AllowsNative("bash"))
            tools.Add(Declare("Bash",
                "Executes a bash command in a persistent shell session with optional timeout, ensuring proper handling and security measures.",
                new SchemaBuilder()
                    .Add("command", "string", "The command to execute.", required: true)
                    .Add("description", "string", "Clear, concise description of what this command does in active voice.")
                    .Add("timeout", "number", "Optional timeout in milliseconds.")
                    .Add("run_in_background", "boolean", "Set to true to run this command in the background.")));

        if (FindFiles != null && surface.AllowsNative("find_files"))
            tools.Add(Declare("Glob",
                "Fast file pattern matching tool that works with any codebase size. Returns matching file paths sorted by modification time.",
                new SchemaBuilder()
                    .Add("pattern", "string", "The glob pattern to match files against.", required: true)
                    .Add("path", "string", "The directory to search in. Defaults to the current working directory.")));

        if (Grep != null && surface.AllowsNative("grep"))
            tools.Add(Declare("Grep",
                "A powerful search tool built on ripgrep. Supports full regex syntax and filtering by file type or glob.",
                new SchemaBuilder()
                    .Add("pattern", "string", "The regular expression pattern to search for in file contents.", required: true)
                    .Add("path", "string", "File or directory to search in. Defaults to the current working directory.")
                    .Add("glob", "string", "Glob pattern to filter files (e.g. \"*.js\").")
                    .Add("type", "string", "File type to search (e.g. \"js\", \"py\", \"rust\").")
                    .AddEnum("output_mode", new[] { "content", "files_with_matches", "count" },
                        "Output mode: \"content\" shows matching lines, \"files_with_matches\" shows file paths, \"count\" shows match counts.")
                    .Add("-i", "boolean", "Case insensitive search.")
                    .Add("-n", "boolean", "Show line numbers in output. Requires output_mode: \"content\".")
                    .Add("-A", "number", "Number of lines to show after each match.")
                    .Add("-B", "number", "Number of lines to show before each match.")
                    .Add("-C", "number", "Number of lines to show before and after each match.")
                    .Add("head_limit", "number", "Limit output to the first N lines or entries.")
                    .Add("multiline", "boolean", "Enable multiline mode where . matches newlines.")));

        if (ReadFile != null && surface.AllowsNative("read_file"))
            tools.Add(Declare("Read",
                "Reads a file from the local filesystem. Results are returned using cat -n format, with line numbers starting at 1.",
                new SchemaBuilder()
                    .Add("file_path", "string", "The absolute path to the file to read.", required: true)
                    .Add("offset", "number", "The line number to start reading from.")
                    .Add("limit", "number", "The number of lines to read.")));

        if (EditFile != null && surface.AllowsNative("edit_file"))
            tools.Add(Declare("Edit",
                "Performs exact string replacements in files. You must Read the file before editing it.",
                new SchemaBuilder()
                    .Add("file_path", "string", "The absolute path to the file to modify.", required: true)
                    .Add("old_string", "string", "The text to replace.", required: true)
                    .Add("new_string", "string", "The text to replace it with (must be different from old_string).", required: true)
                    .Add("replace_all", "boolean", "Replace all occurrences of old_string (default false).")));

        if (WriteFile != null && surface.AllowsNative("write_file"))
            tools.Add(Declare("Write",
                "Writes a file to the local filesystem, overwriting if one exists. You must Read an existing file before overwriting it.",
                new SchemaBuilder()
                    .Add("file_path", "string", "The absolute path to the file to write.", required: true)
                    .Add("content", "string", "The content to write to the file.", required: true)));

        if (AnyNativeToolAllowed(surface))
            tools.Add(Declare("NotebookEdit",
                "Replaces the contents of a specific cell in a Jupyter notebook.",
                new SchemaBuilder()
                    .Add("notebook_path", "string", "The absolute path to the notebook file.", required: true)
                    .Add("new_source", "string", "The new source for the cell.", required: true)
                    .Add("cell_id", "string", "The id of the cell to edit.")
                    .AddEnum("cell_type", new[] { "code", "markdown" }, "The type of the cell.")
                    .AddEnum("edit_mode", new[] { "replace", "insert", "delete" }, "The type of edit to make.")));

        if (FetchUrl != null && surface.AllowsNative("fetch_url"))
            tools.Add(Declare("WebFetch",
                "Fetches content from a specified URL and processes it with a prompt.",
                new SchemaBuilder()
                    .Add("url", "string", "The URL to fetch content from.", required: true)
                    .Add("prompt", "string", "The prompt to run on the fetched content.", required: true)));

        if (SearchWeb != null && surface.AllowsNative("search_web"))
            tools.Add(Declare("WebSearch",
                "Search the web and use the results to inform responses.",
                new SchemaBuilder()
                    .Add("query", "string", "The search query to use.", required: true)
                    .AddStringArray("allowed_domains", "Only include search results from these domains.")
                    .AddStringArray("blocked_domains", "Never include search results from these domains.")));

        if (surface.AllowsNative("todo"))
            tools.Add(Declare("TodoWrite",
                "Use this tool to create and manage a structured task list for your current work session.",
                new SchemaBuilder()
                    .AddArray("todos", "The updated todo list.", required: true, item: new SchemaBuilder()
                        .Add("content", "string", "The imperative form describing the task.", required: true)
                        .AddEnum("status", new[] { "pending", "in_progress", "completed" }, "The task status.", required: true)
                        .Add("activeForm", "string", "The present continuous form of the description.", required: true))));

        if (AnyNativeToolAllowed(surface))
            tools.Add(Declare("Skill",
                "Execute a skill: a packaged set of instructions for a particular kind of task.",
                new SchemaBuilder()
                    .Add("skill", "string", "The name of the skill to invoke.", required: true)
                    .Add("args", "string", "Optional arguments for the skill.")));

        return tools;
    }

    /// <summary>
    /// The declared-but-unbacked tools have no canonical counterpart to filter on, so they
    /// ride the surface as a group: present by default, gone under <c>tools none</c>. Any
    /// finer control would need nb's vocabulary to grow names for tools nb does not have.
    /// </summary>
    private static bool AnyNativeToolAllowed(ToolSurface surface) =>
        surface.NativeAllow is null || surface.NativeAllow.Count > 0;

    private static AIFunction Declare(string name, string description, SchemaBuilder schema) =>
        new DeclaredFunction(name, description, schema.Build());

    protected override async Task<ToolOutcome?> DispatchAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "Bash" when Bash != null:
                // run_in_background and timeout are accepted and ignored.
                return await HandleBashToolCall(callId, Str(arguments, "command"), Str(arguments, "description"));

            case "Glob" when FindFiles != null:
                return HandleFindFilesToolCall(callId, Str(arguments, "pattern"), StrOrNull(arguments, "path"), null);

            case "Grep" when Grep != null:
                // type, multiline, -n and the context flags are accepted and ignored;
                // `count` has no nb equivalent and degrades to a file list.
                return HandleGrepToolCall(callId, Str(arguments, "pattern"), StrOrNull(arguments, "path"),
                    StrOrNull(arguments, "glob"), Bool(arguments, "-i"), Int(arguments, "head_limit"),
                    StrOrNull(arguments, "output_mode") switch
                    {
                        "files_with_matches" or "count" => "files_with_matches",
                        _ => "content",
                    });

            case "Read" when ReadFile != null:
                return HandleReadFileToolCall(callId, Str(arguments, "file_path"),
                    Int(arguments, "offset"), Int(arguments, "limit"));

            case "Edit" when EditFile != null:
                return HandleEditFileToolCall(callId, Str(arguments, "file_path"), Str(arguments, "old_string"),
                    Str(arguments, "new_string"), Bool(arguments, "replace_all") ?? false);

            case "Write" when WriteFile != null:
                return await HandleWriteFileToolCall(callId, Str(arguments, "file_path"), Str(arguments, "content"));

            case "WebFetch" when FetchUrl != null:
                return await HandleFetchUrlToolCall(callId, Str(arguments, "url"));

            case "WebSearch" when SearchWeb != null:
                return await HandleSearchWebToolCall(callId, Str(arguments, "query"));

            case "TodoWrite":
                return HandleTodoWriteToolCall(callId,
                    AsWholeListReplacement(ParseChecklist(arguments, "todos", "content")));

            // Declared so the model sees the surface it expects; not backed. Saying so in
            // the result beats returning a plausible fake, which would put a fabricated
            // answer into the transcript and make the run silently meaningless.
            case "Task":
            case "Skill":
            case "NotebookEdit":
                return ToolOutcome.Fail(callId,
                    $"Error: {name} is declared by this harness costume but is not implemented. "
                    + "nb advertises it so the tool surface matches Claude Code's; nothing ran. "
                    + "Do not retry — accomplish the task with the other tools.");

            default:
                return null;
        }
    }
}
