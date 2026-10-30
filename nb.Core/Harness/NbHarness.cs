using System.Text.Encodings.Web;
using Microsoft.Extensions.AI;
using nb.Shell;
using System.Text.Json;
using nb.Utilities;
using Spectre.Console;
using nb.Shell.ApplyPatch;
using nb.Transcript;

namespace nb.Harness;

/// <summary>
/// A dispatched tool call's outcome: the content returned to the model plus whether it
/// represents a failure. IsError is decided where the result is built — a tool's Success
/// flag, a denial, a timeout, a caught exception — so error accounting never has to
/// sniff the result text for an "Error:" prefix.
/// </summary>
public readonly record struct ToolOutcome(FunctionResultContent Content, bool IsError)
{
    public static ToolOutcome Ok(string callId, object? result) => new(new FunctionResultContent(callId, result), false);
    public static ToolOutcome Fail(string callId, string message) => new(new FunctionResultContent(callId, message), true);
}

/// <summary>
/// The tool surface a run advertises to the model — nb's own, and since the costumes
/// retired (plans/atif-and-acp.md stage 1) the only one — and the capabilities that run
/// those tools under approval, the read-tracker and the trust sandbox. MCP tools are not
/// part of it: they are user configuration.
/// </summary>
public sealed class NbHarness
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
        ApplyPatchTool? applyPatch = null,
        bool applyPatchStyle = false)
    {
        ApplyPatchStyle = applyPatchStyle;
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

    // Null only when not wired: --nobash nulls the lot.
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

    /// <summary>
    /// <c>EditToolStyle: ApplyPatch</c> on the active provider entry. It selects which
    /// edit surface nb's own harness advertises — apply_patch, or write_file + edit_file —
    /// and nothing else; both are always constructed.
    /// </summary>
    public bool ApplyPatchStyle { get; }

    /// <summary>Todo state is per-run and rides the tool surface, so the harness owns it.</summary>
    public TodoManager Todos => _todos;
    public TodoTool Todo => _todoTool;

    // ---- Execution state -------------------------------------------------------
    // The capabilities below run tools, which means they own approval, the
    // read-before-edit tracker and the trust sandbox.

    private ApprovalPolicy _approvalPolicy = null!;
    private bool _verbose;

    /// <summary>Read-before-edit bookkeeping. Per-run, and tied to the file tools.</summary>
    public FileReadTracker Files { get; } = new();

    /// <summary>The resolved approval policy — the <c>approval</c> directive layers onto it.</summary>
    public ApprovalPolicy ApprovalPolicy => _approvalPolicy;

    /// <summary>
    /// Hand the harness the run-level execution context. Called by the runtime.
    ///
    /// Trust is not passed separately: it reaches tool calls through
    /// <see cref="ApprovalPolicy"/>, which owns the whole auto-approve ladder.
    /// </summary>
    public void Configure(ApprovalPolicy approvalPolicy, bool verbose, ApprovalLedger? approvals = null)
    {
        _approvalPolicy = approvalPolicy;
        _verbose = verbose;
        _approvals = approvals;
    }

    /// <summary>
    /// Where approval verdicts are recorded for the transcript. Null in contexts that emit
    /// no transcript (tests constructing a bare harness). Owned by the run, not the harness,
    /// so it survives a <c>harness</c> directive swapping the surface mid-run.
    /// </summary>
    private ApprovalLedger? _approvals;

    /// <summary>
    /// Assemble the native tools this harness advertises, filtered by the surface a
    /// <c>tools</c> directive folded. Order is meaningful — it is the order the model
    /// sees — and is pinned by <c>ToolSurfaceGoldenTests</c>.
    /// </summary>
    public IReadOnlyList<AIFunction> CreateTools(ToolSurface surface)
    {
        var tools = new List<AIFunction>();

        if (Bash != null && surface.AllowsNative("bash")) tools.Add(Bash.CreateTool());
        if (ReadFile != null && surface.AllowsNative("read_file")) tools.Add(ReadFile.CreateTool());
        // The two edit surfaces are mutually exclusive on nb's own harness — GPT-family
        // models confuse them when both are offered — so ApplyPatchStyle picks one.
        if (!ApplyPatchStyle)
        {
            if (WriteFile != null && surface.AllowsNative("write_file")) tools.Add(WriteFile.CreateTool());
            if (EditFile != null && surface.AllowsNative("edit_file")) tools.Add(EditFile.CreateTool());
        }
        if (FindFiles != null && surface.AllowsNative("find_files")) tools.Add(FindFiles.CreateTool());
        if (Grep != null && surface.AllowsNative("grep")) tools.Add(Grep.CreateTool());
        if (ListDir != null && surface.AllowsNative("list_dir")) tools.Add(ListDir.CreateTool());
        if (ApplyPatchStyle && ApplyPatch != null && surface.AllowsNative("apply_patch")) tools.Add(ApplyPatch.CreateTool());
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
    /// <summary>
    /// Run a tool this harness advertises, by the name it advertised it under, and log
    /// the round once. Returns null when the tool is not the harness's to run (MCP, fake
    /// tools, the nb_ resource pair) — the caller's signal to keep looking.
    /// </summary>
    public async Task<ToolOutcome?> InvokeAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken = default)
    {
        var outcome = await DispatchAsync(name, callId, arguments, cancellationToken);
        if (outcome is { } o) LogToolCall(name, arguments, DescribeForLog(o));
        return outcome;
    }

    private static string DescribeForLog(ToolOutcome outcome) => outcome.Content.Result switch
    {
        string s => s.Length > 200 ? s[..200] + "..." : s,
        IList<AIContent> parts => $"[{parts.Count} content part(s)]",
        var other => other?.ToString() ?? "",
    };

    /// <summary>
    /// Map an advertised tool name and its arguments onto the capabilities.
    /// </summary>
    private async Task<ToolOutcome?> DispatchAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "bash" when Bash != null:
                return await HandleBashToolCall(callId, Str(arguments, "command"), Str(arguments, "description"),
                    Int(arguments, "timeout_seconds"), cancellationToken);

            case "read_file" when ReadFile != null:
                return HandleReadFileToolCall(callId, Str(arguments, "path"),
                    Int(arguments, "offset"), Int(arguments, "limit"));

            case "write_file" when WriteFile != null:
                return await HandleWriteFileToolCall(callId, Str(arguments, "path"), Str(arguments, "content"));

            case "edit_file" when EditFile != null:
                return HandleEditFileToolCall(callId, Str(arguments, "path"), Str(arguments, "old_string"),
                    Str(arguments, "new_string"), Bool(arguments, "replace_all") ?? false);

            case "find_files" when FindFiles != null:
                return HandleFindFilesToolCall(callId, Str(arguments, "pattern"),
                    StrOrNull(arguments, "path"), Int(arguments, "max_results"));

            case "grep" when Grep != null:
                return HandleGrepToolCall(callId, Str(arguments, "pattern"), StrOrNull(arguments, "path"),
                    StrOrNull(arguments, "file_pattern"), Bool(arguments, "case_insensitive"),
                    Int(arguments, "max_results"), StrOrNull(arguments, "output_mode"));

            case "list_dir" when ListDir != null:
                return HandleListDirToolCall(callId, StrOrNull(arguments, "path"));

            case "search_web" when SearchWeb != null:
                return await HandleSearchWebToolCall(callId, Str(arguments, "query"));

            case "fetch_url" when FetchUrl != null:
                return await HandleFetchUrlToolCall(callId, Str(arguments, "url"));

            case "apply_patch" when ApplyPatch != null:
                return HandleApplyPatchToolCall(callId, Str(arguments, "input"));

            case "todo_write":
                return HandleTodoWriteToolCall(callId, arguments);

            case "todo_read":
                return HandleTodoReadToolCall(callId);

            default:
                return null;
        }
    }

    // ---- Result formatting -------------------------------------------------------
    // The exact text a tool call returns is the *observation* half of the loop, and
    // models read it as closely as the action half; the golden masters pin it.

    /// <summary>What the model sees after a shell command runs.</summary>
    private string FormatShellResult(ShellResult result)
    {
        var output = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(result.Stdout)) output.AppendLine(result.Stdout);
        if (!string.IsNullOrEmpty(result.Stderr)) output.AppendLine($"[stderr]\n{result.Stderr}");

        output.AppendLine($"\n[exit code: {result.ExitCode}]");
        if (result.Truncated) output.AppendLine("[output was truncated]");
        if (result.TimedOut) output.AppendLine("[command timed out]");

        return output.ToString().Trim();
    }

    /// <summary>What the model sees after a file is written. <paramref name="created"/> distinguishes a new file from an overwrite.</summary>
    private string FormatWriteResult(string path, long bytesWritten, bool created) =>
        $"Successfully wrote {bytesWritten} bytes to {path}";

    /// <summary>What the model sees after a targeted edit lands.</summary>
    private string FormatEditResult(string path, int replacements) =>
        replacements == 1
            ? $"Successfully edited {path} (1 replacement)"
            : $"Successfully edited {path} ({replacements} replacements)";

    /// <summary>What the model sees after a patch applies.</summary>
    private string FormatApplyPatchResult(PatchPreview preview)
    {
        var added = preview.Files.Count(f => f.Kind == FileOpKind.Add);
        var updated = preview.Files.Count(f => f.Kind == FileOpKind.Update || f.Kind == FileOpKind.UpdateAndMove);
        var deleted = preview.Files.Count(f => f.Kind == FileOpKind.Delete);
        return $"Applied patch: {added} added, {updated} updated, {deleted} deleted";
    }

    // ---- Argument unpacking ------------------------------------------------------
    // A model can omit an argument its schema calls required, so every reader tolerates
    // a missing key rather than throwing (a bash-arm bug found 2026-08 under a since-retired costume).

    private static string Str(IDictionary<string, object?>? args, string key) => StrOrNull(args, key) ?? "";

    private static string? StrOrNull(IDictionary<string, object?>? args, string key) =>
        args != null && args.TryGetValue(key, out var v) ? v?.ToString() : null;


    private static int? Int(IDictionary<string, object?>? args, string key) =>
        StrOrNull(args, key) is { Length: > 0 } s && int.TryParse(s, out var n) ? n : null;

    private static bool? Bool(IDictionary<string, object?>? args, string key) =>
        StrOrNull(args, key) is { Length: > 0 } s ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : null;



    // ---- Read-side capabilities -------------------------------------------------
    // Each owns its approval check and its console chrome; DispatchAsync owns argument
    // unpacking.

    /// <summary>Read a file. Auto-approves inside the cwd sandbox, denies outside it.</summary>
    private ToolOutcome HandleReadFileToolCall(string callId, string path, int? offset, int? limit)
    {
        var fullReadPath = ReadFile!.ResolvePath(path);
        if (!ApproveReadPath("Read", fullReadPath, ReadFile.GetCwd()))
            return Deny(callId, $"read_file → {path}", DenyRung, OutsideCwdRemedy);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• reading {Markup.Escape(path)}[/]");

        var readResult = ReadFile.ReadFile(path, offset, limit);
        if (!readResult.Success)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(readResult.Error ?? "Unknown error")}[/]");
            return ToolOutcome.Fail(callId, $"Error: {readResult.Error}");
        }

        if (readResult.FileType == "image")
        {
            Files.RecordRead(readResult.Path);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → image ({readResult.ImageSizeBytes:N0} bytes)[/]");
            var imageBytes = Convert.FromBase64String(readResult.ImageBase64!);
            var imageContent = new DataContent(imageBytes, readResult.MimeType!);
            var textNote = new TextContent($"[Image loaded: {Path.GetFileName(path)} ({readResult.ImageSizeBytes:N0} bytes)]");
            return ToolOutcome.Ok(callId, new List<AIContent> { textNote, imageContent });
        }

        Files.RecordRead(readResult.Path);
        var label = readResult.FileType == "pdf"
            ? $"{readResult.TotalLines} pages"
            : $"{readResult.LinesReturned} lines ({readResult.TotalLines} total)";
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {label}[/]");
        return ToolOutcome.Ok(callId, readResult.Content ?? "");
    }

    /// <summary>Glob for files. Auto-approves inside the cwd sandbox, denies outside it.</summary>
    private ToolOutcome HandleFindFilesToolCall(string callId, string pattern, string? path, int? maxResults)
    {
        var fullFindPath = FindFiles!.ResolvePath(path);
        if (!ApproveReadPath("Find", fullFindPath, FindFiles.GetCwd()))
            return Deny(callId, $"find_files → {path ?? "."}", DenyRung, OutsideCwdRemedy);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• find_files: {Markup.Escape(pattern)}[/]");

        var findResult = FindFiles.FindFiles(pattern, path, maxResults);
        return ReportReadResult(callId, findResult.Success, findResult.Output, findResult.Error,
            () => $"{findResult.Files.Length} files ({findResult.TotalMatches} total)");
    }

    /// <summary>Regex search across files. Auto-approves inside the cwd sandbox, denies outside it.</summary>
    private ToolOutcome HandleGrepToolCall(string callId, string pattern, string? path, string? filePattern,
        bool? caseInsensitive, int? maxResults, string? outputMode)
    {
        var fullGrepPath = Grep!.ResolvePath(path);
        if (!ApproveReadPath("Grep", fullGrepPath, Grep.GetCwd()))
            return Deny(callId, $"grep → {path ?? "."}", DenyRung, OutsideCwdRemedy);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• grep: {Markup.Escape(pattern)}{(filePattern != null ? $" ({Markup.Escape(filePattern)})" : "")}[/]");

        var grepResult = Grep.Grep(pattern, path, filePattern, caseInsensitive, maxResults, outputMode);
        return ReportReadResult(callId, grepResult.Success, grepResult.Output, grepResult.Error,
            () => $"{grepResult.Matches.Length} matches ({grepResult.TotalMatches} total)");
    }

    /// <summary>List a directory. Auto-approves inside the cwd sandbox, denies outside it.</summary>
    private ToolOutcome HandleListDirToolCall(string callId, string? path)
    {
        var fullListPath = ListDir!.ResolvePath(path);
        if (!ApproveReadPath("List", fullListPath, ListDir.GetCwd()))
            return Deny(callId, $"list_dir → {path ?? "."}", DenyRung, OutsideCwdRemedy);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• list_dir: {Markup.Escape(path ?? ".")}[/]");

        var listResult = ListDir.ListDir(path);
        return ReportReadResult(callId, listResult.Success, listResult.Output, listResult.Error,
            () => $"{listResult.Output?.Split('\n').Length ?? 0} entries");
    }

    /// <summary>
    /// Render a read-side tool's outcome and turn it into a <see cref="ToolOutcome"/>.
    /// find_files, grep and list_dir differ only in the count they report, so the
    /// success/failure branch lives here once rather than three times.
    ///
    /// <paramref name="summary"/> is deferred because it reads result fields that only
    /// carry meaning on success — evaluating it eagerly would run them on the error path
    /// too, for a string that is then thrown away.
    /// </summary>
    private static ToolOutcome ReportReadResult(
        string callId, bool success, string? output, string? error, Func<string> summary)
    {
        if (success)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {summary()}[/]");
            return ToolOutcome.Ok(callId, output ?? "");
        }

        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(error ?? "Unknown error")}[/]");
        return ToolOutcome.Fail(callId, $"Error: {error}");
    }

    /// <summary>Record todo changes. Always auto-approves.</summary>
    private ToolOutcome HandleTodoWriteToolCall(string callId, IDictionary<string, object?>? arguments)
    {
        var changes = ParseTodoChanges(arguments);
        var resultString = Todo.Write(changes);
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_write ({changes.Count} change(s))[/]");
        return ToolOutcome.Ok(callId, resultString);
    }

    /// <summary>Read the todo list. Always auto-approves.</summary>
    private ToolOutcome HandleTodoReadToolCall(string callId)
    {
        var resultString = Todo.Read();
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_read[/]");
        return ToolOutcome.Ok(callId, resultString);
    }

    /// <summary>
    /// Which <see cref="ApprovalLedger"/> rung a denial belongs to, and so which explanation
    /// the refusal carries: the program asked for <c>approval default deny</c>, or the call
    /// simply climbed the whole ladder without matching anything.
    ///
    /// This reads <see cref="ApprovalPolicy.Default"/> rather than the decision because
    /// <see cref="ApprovalDecision"/> no longer distinguishes them — both tiers deny, and
    /// the tier is the only thing that still knows why.
    /// </summary>
    public string DenyRung => _approvalPolicy.Default == ApprovalDefault.Deny
        ? ApprovalLedger.DefaultDeny
        : ApprovalLedger.NoMatch;

    /// <summary>
    /// The one refusal, written for two readers.
    ///
    /// *The human* gets the refusal plus the line that would have authorized it — the half
    /// that did not exist before. A directive is pasteable into the program; where no
    /// directive can grant the thing, say so plainly rather than inventing one.
    ///
    /// *The model* gets <see cref="RefusalText"/>.
    /// </summary>
    /// <param name="tool">What was refused, as the reader sees it — "bash (run)", "write_file → /etc/motd".</param>
    /// <param name="rung">The <see cref="ApprovalLedger"/> rung that decided it; also selects the explanation.</param>
    /// <param name="remedy">
    /// How to authorize it. An <c>approval …</c> line is offered as pasteable; anything
    /// else is printed as guidance. Never name a directive that does not exist — the
    /// grammar is <c>bash | mcp | search | fetch | default | sandbox</c>, and there is no
    /// <c>approval path</c>.
    /// </param>
    /// <param name="allowed">For the model only: what this run does allow, so one refusal is not read as the whole tool being off.</param>
    public ToolOutcome Deny(string callId, string tool, string rung, string remedy, string? miss = null, string? allowed = null)
    {
        // The ledger keeps the bare rung as the leading token so `approval_reason` stays
        // greppable by prefix — `no-match` still matches `no-match (…)`. The detail is
        // strictly additional information, not a renamed value.
        // bugs/Denials_Do_Not_Name_The_Near_Miss.md
        _approvals?.RecordDeny(callId, miss is null ? rung : $"{rung} ({miss})");

        var pasteable = IsDirective(remedy);
        Console.Error.WriteLine($"[nb] denied: {tool} — {Because(rung)}.");
        if (miss is not null)
            Console.Error.WriteLine($"       near miss: {miss}");
        Console.Error.WriteLine(pasteable ? $"       authorize with: {remedy}" : $"       {remedy}");

        return ToolOutcome.Fail(callId, RefusalText(tool, rung, remedy, miss, allowed));
    }

    /// <summary>Why the call was refused, in nb's own terms. Shared by both audiences.</summary>
    private static string Because(string rung) => rung == ApprovalLedger.DefaultDeny
        ? "the approval policy default is deny, and no allow-rule matched"
        : "nothing in the approval policy allows it";

    /// <summary>Whether a remedy is a pasteable <c>approval …</c> line rather than prose guidance.</summary>
    private static bool IsDirective(string remedy) => remedy.StartsWith("approval ", StringComparison.Ordinal);

    /// <summary>
    /// The refusal the model reads. nb's own is **terminal**: what was refused, which rule
    /// refused it, and that retrying is pointless because nothing in the run will change.
    /// The verdict is scoped to the call: unscoped, a model reads it as the whole tool
    /// being off and stops using what it is allowed (breitreiter/nb#2).
    /// Nobody is going to say yes mid-run, so a refusal implying otherwise buys wasted
    /// turns. Naming the deciding rule follows qwen-code, which puts its matching deny
    /// rule in the string the model reads.
    /// </summary>
    private static string RefusalText(string tool, string rung, string remedy, string? miss = null, string? allowed = null) =>
        $"Error: {tool} was denied — {Because(rung)}.{(miss is null ? "" : $" Near miss: {miss}.")} " +
        "Retrying this call will not succeed; nothing in this run will grant it. " +
        "Each call is decided on its own, so other calls the policy allows still run. " +
        (allowed is null ? "" : $"{allowed} ") +
        $"{(IsDirective(remedy) ? $"It would require this directive in the program: {remedy}." : remedy)} " +
        "Continue with what you are authorized to do, or report that the operation is not permitted.";

    private const int AllowedBashShown = 20;

    /// <summary>
    /// The bash patterns that do allow commands, named in the refusal: told only that other
    /// calls may run, Haiku still concluded bash was off for the run (breitreiter/nb#2).
    /// Null with none configured. The safe-command list and trust are not named; under
    /// default deny, where this matters most, they do not apply.
    /// </summary>
    private static string? AllowedBash(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0) return null;
        var shown = string.Join(", ", patterns.Take(AllowedBashShown).Select(p => $"`{p}`"));
        var more = patterns.Count > AllowedBashShown ? $", and {patterns.Count - AllowedBashShown} more" : "";
        return $"Bash commands this run allows: {shown}{more}. A pattern matches the whole command line, from its first word.";
    }

    /// <summary>The <c>approval bash</c> line that would have allowed this command, keyed to its program.</summary>
    private static string BashRemedy(string command)
    {
        var program = command.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrEmpty(program) ? "approval bash <pattern>" : $"approval bash {program} *";
    }

    /// <summary>
    /// A path outside the working directory. There is deliberately no remedy directive
    /// here: no <c>approval</c> key widens the path sandbox, and <c>"Trust": true</c>
    /// only reaches cwd + system temp. Saying so is the honest answer.
    /// </summary>
    private const string OutsideCwdRemedy =
        "No approval directive grants paths outside the working directory. Work within it, " +
        "or start nb from a directory that contains the target.";

    /// <summary>
    /// Approve and run one bash call. <paramref name="timeoutSeconds"/> is the model's
    /// requested timeout in *seconds* (bugs/Bash_Advertises_A_Timeout_It_Ignores.md).
    /// </summary>
    public async Task<ToolOutcome> HandleBashToolCall(string callId, string command, string description,
        int? timeoutSeconds = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Classify the command for display
            var classified = CommandClassifier.Classify(command);

            // The approval policy owns the auto-approve precedence (--approve → safe
            // allowlist → trust+sandbox). Allow carries a reason for the log line.
            var cwd = Bash?.GetCwd() ?? "";
            var (decision, approveReason, miss) = _approvalPolicy.DecideBash(command, classified, cwd, Bash != null);
            if (decision == ApprovalDecision.Allow)
            {
                _approvals?.RecordAllow(callId, approveReason ?? ApprovalLedger.Safe);
                var display = Markup.Escape(classified.DisplayText);
                var line = approveReason switch
                {
                    "pre-approved" => $"• bash (pre-approved): {display}",
                    "trust" => $"• auto: bash {display}",
                    _ => $"• bash: {display}",
                };
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{line}[/]");
                return await ExecuteBashCommand(callId, command, timeoutSeconds, cancellationToken);
            }

            // Nothing on the ladder allowed it, so it is refused. The classification and
            // danger reason still print: they are the observation channel that tells a human
            // reading the run what was attempted, and they are more useful on a denial than
            // on an approval.
            if (!string.IsNullOrWhiteSpace(description))
                AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]{Markup.Escape(description)}[/]");

            // A dangerous command shows in full — the summary would hide what was refused.
            var displayCommand = classified.IsDangerous ? command : classified.DisplayText;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{classified.Category}:[/] {Markup.Escape(displayCommand)}");

            if (classified.IsDangerous && classified.DangerReason != null)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: {classified.DangerReason}[/]");

            return Deny(callId, $"bash ({classified.Category}): {classified.DisplayText}",
                DenyRung, BashRemedy(command), miss, AllowedBash(_approvalPolicy.BashPatterns));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Approval error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during command approval: {ex.Message}");
        }
    }

    private async Task<ToolOutcome> ExecuteBashCommand(string callId, string command, int? timeoutSeconds, CancellationToken cancellationToken)
    {
        if (Bash == null)
        {
            return ToolOutcome.Fail(callId, "Error: Bash tool not initialized");
        }

        try
        {
            var result = await Bash.ExecuteAsync(command, cwd: null, timeoutSeconds, cancellationToken);

            var outputStr = FormatShellResult(result);

            // Show brief status to user
            var statusColor = result.ExitCode == 0 ? UIColors.SpectreSuccess : UIColors.SpectreWarning;
            var statusIcon = result.ExitCode == 0 ? "✓" : "✗";
            AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon}[/] [{UIColors.SpectreMuted}]exit {result.ExitCode}[/]");

            // A non-zero exit is not flagged as a tool error — it's a normal shell
            // outcome the model should read, matching the prior "Error:"-prefix rule.
            return ToolOutcome.Ok(callId, outputStr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error executing command: {ex.Message}");
        }
    }

    public Task<ToolOutcome> HandleWriteFileToolCall(string callId, string path, string content)
    {
        try
        {
            // Resolve path for display
            var fullPath = WriteFile?.ResolvePath(path) ?? path;

            // Captured before the write: the acknowledgment distinguishes creating a file
            // from overwriting one.
            var created = !File.Exists(fullPath);

            // Guard: if file exists, it must have been read first
            if (!created)
            {
                if (!Files.HasBeenRead(fullPath))
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: You must read_file before overwriting an existing file."));

                if (Files.HasBeenModifiedSinceRead(fullPath))
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: File has been modified since you last read it. Read it again before writing."));
            }

            var lineCount = content.Split('\n').Length;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);

            // Auto-approve writes within cwd sandbox
            if (Bash != null)
            {
                var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, Bash.GetCwd());
                if (symlinkEscape)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
                    // Fall through to manual approval
                }
                else if (trusted)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• write {Markup.Escape(fullPath)} ({lineCount} lines)[/]");

                    if (WriteFile == null)
                        return Task.FromResult(ToolOutcome.Fail(callId, "Error: Write file tool not initialized"));

                    var writeResult = WriteFile.WriteFile(path, content);
                    if (writeResult.Success)
                    {
                        Files.RecordWrite(writeResult.Path);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]wrote {writeResult.BytesWritten} bytes[/]");
                        return Task.FromResult(ToolOutcome.Ok(callId, FormatWriteResult(writeResult.Path, writeResult.BytesWritten, created)));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(writeResult.Error ?? "Unknown error")}[/]");
                        return Task.FromResult(ToolOutcome.Fail(callId, $"Error writing file: {writeResult.Error}"));
                    }
                }
            }

            // Outside the sandbox (or reaching outside it via symlink), so it is refused.
            // The size line stays: it says what was refused, not merely that something was.
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Write:[/] {Markup.Escape(fullPath)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  {lineCount} lines, {byteCount} bytes[/]");

            return Task.FromResult(Deny(callId, $"write_file → {path}", DenyRung, OutsideCwdRemedy));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Write error: {Markup.Escape(ex.Message)}[/]");
            return Task.FromResult(ToolOutcome.Fail(callId, $"Error during file write: {ex.Message}"));
        }
    }

    public async Task<ToolOutcome> HandleSearchWebToolCall(string callId, string query)
    {
        try
        {
            if (_approvalPolicy.DecideSearch() != ApprovalDecision.Allow)
                return Deny(callId, $"search_web: {query}", DenyRung, "approval search allow");

            var result = await SearchWeb!.SearchAsync(query);

            // A missing backend is Success — it is a configuration state, not a failure.
            // Returning it as an error would feed ExitReasons.ToolErrorLimit and abort
            // precisely the runs where the model keeps trying to search.
            if (result.Success)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]OK[/] [{UIColors.SpectreMuted}]{(SearchWeb.HasBackend ? "search complete" : "no backend")}[/]");
                return ToolOutcome.Ok(callId, result.Output ?? SearchWebTool.DeclaredOnlyNote);
            }

            return ToolOutcome.Fail(callId, $"Error: {result.Error}");
        }
        catch (Exception ex)
        {
            return ToolOutcome.Fail(callId, $"Error during search: {ex.Message}");
        }
    }

    public async Task<ToolOutcome> HandleFetchUrlToolCall(string callId, string url)
    {
        try
        {
            if (_approvalPolicy.DecideFetch() != ApprovalDecision.Allow)
                return Deny(callId, $"fetch_url: {url}", DenyRung, "approval fetch allow");

            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Fetch:[/] {Markup.Escape(url)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: outbound network request[/]");

            var result = await FetchUrl!.FetchAsync(url);
            if (result.Success)
            {
                var chars = result.Content?.Length ?? 0;
                var truncNote = result.Truncated ? " (truncated)" : "";
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{chars:N0} chars{truncNote}[/]");
                return ToolOutcome.Ok(callId, result.Content ?? "");
            }
            else
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                return ToolOutcome.Fail(callId, $"Error: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Fetch error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during fetch: {ex.Message}");
        }
    }

    public ToolOutcome HandleEditFileToolCall(string callId, string path, string oldString, string newString, bool replaceAll)
    {
        try
        {
            var fullPath = EditFile?.ResolvePath(path) ?? path;

            // Guard: file must have been read first
            if (!Files.HasBeenRead(fullPath))
                return ToolOutcome.Fail(callId, "Error: You must read_file before editing. Read the file first to see its current content.");

            if (Files.HasBeenModifiedSinceRead(fullPath))
                return ToolOutcome.Fail(callId, "Error: File has been modified since you last read it. Read it again before editing.");

            // Auto-approve edits within cwd sandbox
            if (Bash != null)
            {
                var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, Bash.GetCwd());
                if (symlinkEscape)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
                    // Fall through to manual approval
                }
                else if (trusted)
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• edit {Markup.Escape(fullPath)}[/]");

                    if (EditFile == null)
                        return ToolOutcome.Fail(callId, "Error: Edit file tool not initialized");

                    var editResult = EditFile.EditFile(path, oldString, newString, replaceAll);
                    if (editResult.Success)
                    {
                        Files.RecordWrite(editResult.Path);
                        var msg = FormatEditResult(editResult.Path, editResult.Replacements);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(msg)}[/]");
                        return ToolOutcome.Ok(callId, msg);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(editResult.Error ?? "Unknown error")}[/]");
                        return ToolOutcome.Fail(callId, $"Error: {editResult.Error}");
                    }
                }
            }

            // Outside the sandbox, so it is refused. The diff still prints — seeing the edit
            // that was turned away is the whole value of the display on this path.
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Edit:[/] {Markup.Escape(fullPath)}");

            var oldPreview = oldString.Length > 200 ? oldString[..200] + "..." : oldString;
            var newPreview = newString.Length > 200 ? newString[..200] + "..." : newString;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  - {Markup.Escape(oldPreview)}[/]");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]  + {Markup.Escape(newPreview)}[/]");
            if (replaceAll)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  (replace all occurrences)[/]");

            return Deny(callId, $"edit_file → {path}", DenyRung, OutsideCwdRemedy);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Edit error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during file edit: {ex.Message}");
        }
    }

    public ToolOutcome HandleApplyPatchToolCall(string callId, string input)
    {
        if (ApplyPatch == null)
            return ToolOutcome.Fail(callId, "Error: apply_patch tool not initialized");

        List<FileOp> ops;
        try
        {
            ops = PatchParser.Parse(input);
        }
        catch (PatchParseException ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch parse error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error parsing patch: {ex.Message}");
        }

        var cwd = ApplyPatch.GetCwd();

        PatchPreview preview;
        try
        {
            preview = PatchApplier.BuildPreview(ops, cwd, Files);
        }
        catch (PatchApplyException ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error: {ex.Message}");
        }

        // Check sandbox for every affected absolute path — both source (for updates/deletes) and destination (for moves/adds).
        var allTrusted = true;
        var anyEscape = false;
        for (int i = 0; i < ops.Count; i++)
        {
            var touched = new List<string> { preview.Files[i].FinalPath };
            if (ops[i] is UpdateFile upd && upd.MoveTo != null)
            {
                var sourceFull = Path.IsPathRooted(ops[i].Path)
                    ? Path.GetFullPath(ops[i].Path)
                    : Path.GetFullPath(Path.Combine(cwd, ops[i].Path));
                touched.Add(sourceFull);
            }
            foreach (var p in touched)
            {
                var (trusted, escape) = TrustSandbox.CheckPath(p, cwd);
                if (escape) anyEscape = true;
                if (!trusted) allTrusted = false;
            }
        }

        RenderPatchSummary(preview);

        if (anyEscape)
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ One or more paths resolve outside working directory via symlink — manual approval required[/]");

        if (allTrusted && !anyEscape)
        {
            try
            {
                PatchApplier.Apply(preview, ops, cwd, Files);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]apply_patch failed: {Markup.Escape(ex.Message)}[/]");
                return ToolOutcome.Fail(callId, $"Error applying patch: {ex.Message}");
            }
            var summary = FormatApplyPatchResult(preview);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(summary)}[/]");
            return ToolOutcome.Ok(callId, summary);
        }

        // At least one path leaves the sandbox. RenderPatchSummary above already showed the
        // whole patch, so the human can see exactly which files put it out of bounds.
        return Deny(callId, $"apply_patch (path outside working directory)", DenyRung, OutsideCwdRemedy);
    }

    private static void RenderPatchSummary(PatchPreview preview)
    {
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Patch:[/] {preview.Files.Count} file(s)");
        foreach (var fp in preview.Files)
        {
            var (glyph, label) = fp.Kind switch
            {
                FileOpKind.Add => ("+", "add"),
                FileOpKind.Delete => ("-", "delete"),
                FileOpKind.Update => ("~", "update"),
                FileOpKind.UpdateAndMove => ("↺", "rename+update"),
                _ => ("?", "?"),
            };
            var stats = fp.Kind switch
            {
                FileOpKind.Add => $"{fp.NewLineCount} lines",
                FileOpKind.Delete => $"{fp.OldLineCount} lines",
                _ => $"-{fp.OldLineCount}/+{fp.NewLineCount}",
            };
            var path = fp.Kind == FileOpKind.UpdateAndMove
                ? $"{fp.OriginalPath} → {fp.FinalPath}"
                : fp.FinalPath;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  {glyph} {label,-13} {Markup.Escape(path)} ({stats})[/]");
        }
    }


    private static readonly JsonSerializerOptions _verboseJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public void LogToolCall(string toolName, IDictionary<string, object?>? arguments, string result)
    {
        if (!_verbose) return;

        var argsJson = arguments != null
            ? JsonSerializer.Serialize(arguments, _verboseJsonOptions)
            : "{}";

        // Unescape Unicode sequences for readability (e.g., \u0022 -> ")
        var displayResult = System.Text.RegularExpressions.Regex.Unescape(result);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]┌─ Tool: {Markup.Escape(toolName)}[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]│ Input: {Markup.Escape(argsJson)}[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]└ Output: {Markup.Escape(displayResult)}[/]");
    }

    /// <summary>
    /// Whether a read-side path is inside the trust sandbox (cwd/temp). Outside it, this
    /// renders *why* — the path, and a symlink escape if that is how it got out — and
    /// returns false. The caller turns that false into the refusal itself via
    /// <see cref="Deny"/>, so the stderr line and the model-facing string are written in
    /// one place rather than two that can drift apart.
    /// </summary>
    public bool ApproveReadPath(string toolLabel, string fullPath, string cwd)
    {
        var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, cwd);
        if (_approvalPolicy.DecidePath(trusted && !symlinkEscape) == ApprovalDecision.Allow)
            return true;

        if (symlinkEscape)
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]{toolLabel}:[/] {Markup.Escape(fullPath)}");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: path is outside working directory[/]");
        return false;
    }

    private static readonly JsonSerializerOptions _todoJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Pull a todo argument out as raw JSON, however the provider delivered it — a
    /// <see cref="JsonElement"/> from a structured tool call, or a string from one that
    /// sent the array pre-serialised. Null when the key is absent or carries nothing to
    /// parse, which both callers treat as "no changes submitted".
    /// </summary>
    private static string? TodoArgumentJson(IDictionary<string, object?>? args, string key)
    {
        if (args is null || !args.TryGetValue(key, out var raw) || raw is null) return null;
        var json = raw is JsonElement je ? je.GetRawText() : raw.ToString();
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }

    /// <summary>
    /// Unpack nb's own <c>todo_write</c> argument shape: a <c>changes</c> array of
    /// content/status pairs, already incremental.
    /// </summary>
    public static List<TodoChange> ParseTodoChanges(IDictionary<string, object?>? args)
    {
        var changes = new List<TodoChange>();
        if (TodoArgumentJson(args, "changes") is not { } json) return changes;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<TodoChange>>(json, _todoJsonOptions);
            if (parsed != null) changes.AddRange(parsed);
        }
        catch
        {
            // Leave changes empty; todo_write will report "no changes submitted"
        }
        return changes;
    }
}
