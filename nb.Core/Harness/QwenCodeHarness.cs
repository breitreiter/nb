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
/// Everything downstream of <see cref="ToCanonical"/> keeps seeing nb's canonical names.
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

    public override string Name => HarnessName;

    public override IReadOnlyList<string> Omissions { get; } = new[]
    {
        "system prompt: not yet vendored — qwen-code's prompt is Apache-2.0 and can be used verbatim, but lives in a 76KB TypeScript file with conditional interpolation. This costume is currently tool-surface-only.",
        "tool descriptions: nb's own prose, with corrected parameter names. qwen-code's descriptions are vendorable and not yet vendored.",
        "run_shell_command: is_background and directory are accepted and ignored — nb's bash runs foreground in the shell cwd. timeout is converted from qwen's milliseconds to nb's seconds.",
        "web_fetch: qwen-code runs a model over the fetched page and answers the prompt; nb's fetch_url returns the content. The prompt and format arguments are accepted and ignored.",
        "list_directory: ignore and file_filtering_options are not offered.",
        "surface size: qwen-code advertises ~46 tools (agent, skill, plan mode, cron, sub-sessions, …). This costume covers the file/shell/search core only.",
        "todo_read: not offered — qwen-code declares todo_write with no read counterpart.",
    };

    // wire name -> canonical nb name. Verified against packages/core/src/tools/tool-names.ts.
    private static readonly Dictionary<string, string> WireToCanonical = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = "read_file",
        ["write_file"] = "write_file",
        ["edit"] = "edit_file",
        ["glob"] = "find_files",
        ["grep_search"] = "grep",
        ["list_directory"] = "list_dir",
        ["run_shell_command"] = "bash",
        ["web_fetch"] = "fetch_url",
        ["web_search"] = "search_web",
        ["todo_write"] = "todo_write",
    };

    private static readonly Dictionary<string, string> CanonicalToWire =
        WireToCanonical.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // Per wire tool: wire argument name -> canonical argument name. Arguments absent from
    // a map pass through; arguments mapped to null are dropped (accepted and ignored).
    private static readonly Dictionary<string, Dictionary<string, string?>> ArgumentMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = new() { ["file_path"] = "path" },
        ["write_file"] = new() { ["file_path"] = "path" },
        ["edit"] = new() { ["file_path"] = "path" },
        ["grep_search"] = new() { ["glob"] = "file_pattern", ["limit"] = "max_results" },
        ["run_shell_command"] = new() { ["is_background"] = null, ["directory"] = null, ["timeout"] = "timeout_seconds" },
        ["web_fetch"] = new() { ["prompt"] = null, ["format"] = null },
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

        // apply_patch has no qwen-code counterpart; it stays under its own name so an
        // EditToolStyle: ApplyPatch entry is not silently stripped of its edit tool.
        if (ApplyPatch != null && surface.AllowsNative("apply_patch"))
            tools.Add(ApplyPatch.CreateTool());

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

    public override (string Name, IDictionary<string, object?>? Arguments) ToCanonical(
        string wireName, IDictionary<string, object?>? arguments)
    {
        if (!WireToCanonical.TryGetValue(wireName, out var canonicalName))
            return (wireName, arguments); // MCP / fake / apply_patch — not ours to rename

        if (arguments is null || !ArgumentMaps.TryGetValue(wireName, out var map))
            return (canonicalName, arguments);

        var translated = new Dictionary<string, object?>();
        foreach (var (key, value) in arguments)
        {
            if (!map.TryGetValue(key, out var target))
            {
                translated[key] = value;      // not mentioned: passes through
                continue;
            }
            if (target is null) continue;     // accepted and ignored

            translated[target] = target == "timeout_seconds" ? MillisecondsToSeconds(value) : value;
        }

        return (canonicalName, translated);
    }

    public override string ToWireName(string canonicalName) =>
        CanonicalToWire.TryGetValue(canonicalName, out var wire) ? wire : canonicalName;

    /// <summary>qwen-code's shell timeout is milliseconds; nb's is seconds.</summary>
    private static object? MillisecondsToSeconds(object? value)
    {
        var ms = value switch
        {
            null => (double?)null,
            System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.Number } je => je.GetDouble(),
            IConvertible c => Convert.ToDouble(c),
            _ => null,
        };
        if (ms is null) return value;
        return Math.Max(1, (int)Math.Round(ms.Value / 1000.0));
    }

    private static AIFunction Declare(string name, string? description, SchemaBuilder schema) =>
        new DeclaredFunction(name, description ?? "", schema.Build());
}
