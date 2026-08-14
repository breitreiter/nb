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


    // ---- Execution state -------------------------------------------------------
    // The capabilities below run tools, which means they own approval, the
    // read-before-edit tracker and the trust sandbox. Costumes never touch these:
    // a costume declares tools whose bodies call the capabilities, and the shared
    // base is where the safety concerns stay.

    private ApprovalPolicy _approvalPolicy = null!;
    private bool _trustMode;
    private bool _verbose;

    /// <summary>Read-before-edit bookkeeping. Per-run, and tied to the file tools.</summary>
    public FileReadTracker Files { get; } = new();

    /// <summary>The resolved approval policy — the <c>approval</c> directive layers onto it.</summary>
    public ApprovalPolicy ApprovalPolicy => _approvalPolicy;

    /// <summary>Hand the harness the run-level execution context. Called by the runtime.</summary>
    public void Configure(ApprovalPolicy approvalPolicy, bool trustMode, bool verbose)
    {
        _approvalPolicy = approvalPolicy;
        _trustMode = trustMode;
        _verbose = verbose;
    }

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
    /// Map an advertised tool name and its arguments onto the shared capabilities.
    ///
    /// This is the half a costume overrides: it declares its target's names and schemas
    /// in <see cref="CreateTools"/>, and unpacks its target's argument spellings here
    /// before calling the same capability methods. There is no separate translation
    /// layer — the costume's own dispatch *is* the adaptation.
    /// </summary>
    protected virtual async Task<ToolOutcome?> DispatchAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "bash" when Bash != null:
                return await HandleBashToolCall(callId, Str(arguments, "command"), Str(arguments, "description"));

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

    // ---- Argument unpacking ------------------------------------------------------
    // A model can omit an argument its schema calls required, so every reader tolerates
    // a missing key rather than throwing (see the bash-arm bug the qwen costume exposed).

    protected static string Str(IDictionary<string, object?>? args, string key) => StrOrNull(args, key) ?? "";

    protected static string? StrOrNull(IDictionary<string, object?>? args, string key) =>
        args != null && args.TryGetValue(key, out var v) ? v?.ToString() : null;

    protected static int? Int(IDictionary<string, object?>? args, string key) =>
        StrOrNull(args, key) is { Length: > 0 } s && int.TryParse(s, out var n) ? n : null;

    protected static bool? Bool(IDictionary<string, object?>? args, string key) =>
        StrOrNull(args, key) is { Length: > 0 } s ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : null;



    // ---- Read-side capabilities -------------------------------------------------
    // Extracted so a costume's own tool method can call them. Each owns its approval
    // check and its console chrome; the caller owns argument unpacking, which is where
    // costumes differ.

    /// <summary>Read a file. Auto-approves inside the cwd sandbox, prompts outside it.</summary>
    protected ToolOutcome HandleReadFileToolCall(string callId, string path, int? offset, int? limit)
    {
        var fullReadPath = ReadFile!.ResolvePath(path);
        if (!ApproveReadPath("Read", fullReadPath, ReadFile.GetCwd()))
            return ToolOutcome.Fail(callId, "Error: User rejected read_file. Path is outside working directory.");

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

    /// <summary>Glob for files. Auto-approves inside the cwd sandbox, prompts outside it.</summary>
    protected ToolOutcome HandleFindFilesToolCall(string callId, string pattern, string? path, int? maxResults)
    {
        var fullFindPath = FindFiles!.ResolvePath(path);
        if (!ApproveReadPath("Find", fullFindPath, FindFiles.GetCwd()))
            return ToolOutcome.Fail(callId, "Error: User rejected find_files. Path is outside working directory.");

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• find_files: {Markup.Escape(pattern)}[/]");

        var findResult = FindFiles.FindFiles(pattern, path, maxResults);
        string resultString;
        if (findResult.Success)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {findResult.Files.Length} files ({findResult.TotalMatches} total)[/]");
            resultString = findResult.Output ?? "";
        }
        else
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(findResult.Error ?? "Unknown error")}[/]");
            resultString = $"Error: {findResult.Error}";
        }
        return findResult.Success ? ToolOutcome.Ok(callId, resultString) : ToolOutcome.Fail(callId, resultString);
    }

    /// <summary>Regex search across files. Auto-approves inside the cwd sandbox, prompts outside it.</summary>
    protected ToolOutcome HandleGrepToolCall(string callId, string pattern, string? path, string? filePattern,
        bool? caseInsensitive, int? maxResults, string? outputMode)
    {
        var fullGrepPath = Grep!.ResolvePath(path);
        if (!ApproveReadPath("Grep", fullGrepPath, Grep.GetCwd()))
            return ToolOutcome.Fail(callId, "Error: User rejected grep. Path is outside working directory.");

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• grep: {Markup.Escape(pattern)}{(filePattern != null ? $" ({Markup.Escape(filePattern)})" : "")}[/]");

        var grepResult = Grep.Grep(pattern, path, filePattern, caseInsensitive, maxResults, outputMode);
        string resultString;
        if (grepResult.Success)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {grepResult.Matches.Length} matches ({grepResult.TotalMatches} total)[/]");
            resultString = grepResult.Output ?? "";
        }
        else
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(grepResult.Error ?? "Unknown error")}[/]");
            resultString = $"Error: {grepResult.Error}";
        }
        return grepResult.Success ? ToolOutcome.Ok(callId, resultString) : ToolOutcome.Fail(callId, resultString);
    }

    /// <summary>List a directory. Auto-approves inside the cwd sandbox, prompts outside it.</summary>
    protected ToolOutcome HandleListDirToolCall(string callId, string? path)
    {
        var fullListPath = ListDir!.ResolvePath(path);
        if (!ApproveReadPath("List", fullListPath, ListDir.GetCwd()))
            return ToolOutcome.Fail(callId, "Error: User rejected list_dir. Path is outside working directory.");

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• list_dir: {Markup.Escape(path ?? ".")}[/]");

        var listResult = ListDir.ListDir(path);
        string resultString;
        if (listResult.Success)
        {
            var entryCount = listResult.Output?.Split('\n').Length ?? 0;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  → {entryCount} entries[/]");
            resultString = listResult.Output ?? "";
        }
        else
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  → {Markup.Escape(listResult.Error ?? "Unknown error")}[/]");
            resultString = $"Error: {listResult.Error}";
        }
        return listResult.Success ? ToolOutcome.Ok(callId, resultString) : ToolOutcome.Fail(callId, resultString);
    }

    /// <summary>Record todo changes. Always auto-approves.</summary>
    protected ToolOutcome HandleTodoWriteToolCall(string callId, IDictionary<string, object?>? arguments)
    {
        var changes = ParseTodoChanges(arguments);
        var resultString = Todo.Write(changes);
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_write ({changes.Count} change(s))[/]");
        return ToolOutcome.Ok(callId, resultString);
    }

    /// <summary>Read the todo list. Always auto-approves.</summary>
    protected ToolOutcome HandleTodoReadToolCall(string callId)
    {
        var resultString = Todo.Read();
        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]• todo_read[/]");
        return ToolOutcome.Ok(callId, resultString);
    }

    /// <summary>No TTY on stdin: an unmatched approval is denied rather than prompted.</summary>
    public static bool NonInteractive => Console.IsInputRedirected;


    private static ToolOutcome DenyNonInteractive(string callId, string what)
    {
        Console.Error.WriteLine($"[nb] denied: {what} needs approval, but stdin is not a TTY and nothing pre-approved it.");
        return ToolOutcome.Fail(callId,
            $"Error: {what} requires approval, but this is a non-interactive session (stdin is not a TTY) " +
            "and no pre-approval (--approve/--trust) matched. Permission denied — do not retry; try a different approach.");
    }

    // The approval policy's default is deny: this call matched no allow-rule, so it
    // is refused without a prompt (even interactively). Distinct from the non-TTY
    // deny — it's a deliberate lockdown, not a missing terminal.
    private static ToolOutcome DenyByPolicy(string callId, string what)
    {
        Console.Error.WriteLine($"[nb] denied: {what} — approval policy default is deny and nothing allow-listed it.");
        return ToolOutcome.Fail(callId,
            $"Error: {what} was denied by the approval policy (default: deny) and no allow-rule matched. " +
            "Permission denied — do not retry; try a different approach.");
    }

    public async Task<ToolOutcome> HandleBashToolCall(string callId, string command, string description)
    {
        try
        {
            // Classify the command for display
            var classified = CommandClassifier.Classify(command);

            // The approval policy owns the auto-approve precedence (--approve → safe
            // allowlist → trust+sandbox). Allow carries a reason for the log line.
            var cwd = Bash?.GetCwd() ?? "";
            var (decision, approveReason) = _approvalPolicy.DecideBash(command, classified, cwd, Bash != null);
            if (decision == ApprovalDecision.Allow)
            {
                var display = Markup.Escape(classified.DisplayText);
                var line = approveReason switch
                {
                    "pre-approved" => $"• bash (pre-approved): {display}",
                    "trust" => $"• auto: bash {display}",
                    _ => $"• bash: {display}",
                };
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{line}[/]");
                return await ExecuteBashCommand(callId, command);
            }

            // Policy default is deny: refuse without prompting (even interactively).
            if (decision == ApprovalDecision.Deny)
                return DenyByPolicy(callId, $"bash ({classified.Category})");

            // Show model's description of intent (if provided)
            if (!string.IsNullOrWhiteSpace(description))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreInfo}]{Markup.Escape(description)}[/]");
            }

            // Show approval prompt with command classification
            var categoryColor = classified.IsDangerous ? UIColors.SpectreError : UIColors.SpectreWarning;
            var warningIndicator = classified.IsDangerous ? " ⚠️" : "";

            // For dangerous commands, show the full command so the user can see what they're approving
            var displayCommand = classified.IsDangerous ? command : classified.DisplayText;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]{classified.Category}:[/] {Markup.Escape(displayCommand)}");

            if (classified.IsDangerous && classified.DangerReason != null)
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: {classified.DangerReason}[/]");
            }

            if (NonInteractive)
                return DenyNonInteractive(callId, $"bash ({classified.Category})");

            // Default based on danger level
            var defaultYes = !classified.IsDangerous;
            var options = classified.IsDangerous ? "[[y/N/?]]" : "[[Y/n/?]]";

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? {options}[/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || (!defaultYes && (key == '\r' || key == '\n')))
                {
                    // Rejected
                    var reason = AnsiConsole.Prompt(
                        new TextPrompt<string>($"[{UIColors.SpectreMuted}]Reason (optional):[/]")
                            .DefaultValue("User declined")
                            .AllowEmpty()
                    );

                    var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                        ? "Error: User rejected this command. Permission denied."
                        : $"Error: User rejected this command. Reason: {reason}";

                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Command rejected[/]");
                    return ToolOutcome.Fail(callId, rejectionMessage);
                }
                else if (key == '?')
                {
                    // Show full command
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Full command:[/]");
                    AnsiConsole.WriteLine(command);
                    continue;
                }
                else if (key == 'y' || key == 'Y' || (defaultYes && (key == '\r' || key == '\n')))
                {
                    // Approved
                    return await ExecuteBashCommand(callId, command);
                }
                // For any other key, loop and ask again
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Approval error: {Markup.Escape(ex.Message)}[/]");
            return ToolOutcome.Fail(callId, $"Error during command approval: {ex.Message}");
        }
    }

    private async Task<ToolOutcome> ExecuteBashCommand(string callId, string command)
    {
        if (Bash == null)
        {
            return ToolOutcome.Fail(callId, "Error: Bash tool not initialized");
        }

        try
        {
            var result = await Bash.ExecuteAsync(command);

            // Format result for the model
            var output = new System.Text.StringBuilder();

            if (!string.IsNullOrEmpty(result.Stdout))
            {
                output.AppendLine(result.Stdout);
            }

            if (!string.IsNullOrEmpty(result.Stderr))
            {
                output.AppendLine($"[stderr]\n{result.Stderr}");
            }

            output.AppendLine($"\n[exit code: {result.ExitCode}]");

            if (result.Truncated)
            {
                output.AppendLine("[output was truncated]");
            }

            if (result.TimedOut)
            {
                output.AppendLine("[command timed out]");
            }

            var outputStr = output.ToString().Trim();

            // Show brief status to user
            var statusColor = result.ExitCode == 0 ? UIColors.SpectreSuccess : UIColors.SpectreWarning;
            var statusIcon = result.ExitCode == 0 ? "✓" : "✗";
            AnsiConsole.MarkupLine($"[{statusColor}]{statusIcon}[/] [{UIColors.SpectreMuted}]exit {result.ExitCode}[/]");

            // A non-zero exit is not flagged as a tool error — it's a normal shell
            // outcome the model should read, matching the prior "Error:"-prefix rule.
            return ToolOutcome.Ok(callId, outputStr);
        }
        catch (Exception ex)
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

            // Guard: if file exists, it must have been read first
            if (File.Exists(fullPath))
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
                        return Task.FromResult(ToolOutcome.Ok(callId, $"Successfully wrote {writeResult.BytesWritten} bytes to {writeResult.Path}"));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(writeResult.Error ?? "Unknown error")}[/]");
                        return Task.FromResult(ToolOutcome.Fail(callId, $"Error writing file: {writeResult.Error}"));
                    }
                }
            }

            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return Task.FromResult(DenyByPolicy(callId, "write_file (path outside working directory)"));
            if (NonInteractive)
                return Task.FromResult(DenyNonInteractive(callId, "write_file"));

            // Show approval prompt
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Write:[/] {Markup.Escape(fullPath)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  {lineCount} lines, {byteCount} bytes[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N/?]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    // Rejected (default is No for writes)
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Write rejected[/]");
                    return Task.FromResult(ToolOutcome.Fail(callId, "Error: User rejected file write. Permission denied."));
                }
                else if (key == '?')
                {
                    // Show content preview
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Content preview:[/]");
                    var preview = content.Length > 500 ? content[..500] + "\n... (truncated)" : content;
                    AnsiConsole.WriteLine(preview);
                    continue;
                }
                else if (key == 'y' || key == 'Y')
                {
                    // Approved - execute write
                    if (WriteFile == null)
                    {
                        return Task.FromResult(ToolOutcome.Fail(callId, "Error: Write file tool not initialized"));
                    }

                    var result = WriteFile.WriteFile(path, content);

                    if (result.Success)
                    {
                        Files.RecordWrite(result.Path);
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]wrote {result.BytesWritten} bytes[/]");
                        return Task.FromResult(ToolOutcome.Ok(callId, $"Successfully wrote {result.BytesWritten} bytes to {result.Path}"));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                        return Task.FromResult(ToolOutcome.Fail(callId, $"Error writing file: {result.Error}"));
                    }
                }
            }
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
            var decision = _approvalPolicy.DecideSearch();
            if (decision == ApprovalDecision.Deny)
                return DenyByPolicy(callId, "search_web");

            if (decision != ApprovalDecision.Allow)
            {
                if (NonInteractive)
                    return DenyNonInteractive(callId, "search_web");

                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Search:[/] {Markup.Escape(query)}");
                AnsiConsole.MarkupLine(SearchWeb!.HasBackend
                    ? $"[{UIColors.SpectreWarning}]  Warning: outbound network request[/]"
                    : $"[{UIColors.SpectreMuted}]  No search backend configured — no query will be sent[/]");

                while (Console.KeyAvailable)
                {
                    Console.ReadKey(intercept: true);
                }

                while (true)
                {
                    AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
                    var key = Console.ReadKey().KeyChar;
                    Console.WriteLine();

                    if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                    {
                        var reason = AnsiConsole.Prompt(
                            new Spectre.Console.TextPrompt<string>($"[{UIColors.SpectreMuted}]Reason (optional):[/]")
                                .DefaultValue("User declined")
                                .AllowEmpty());
                        var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                            ? "Error: User rejected search_web. Permission denied."
                            : $"Error: User rejected search_web. Reason: {reason}";
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Search rejected[/]");
                        return ToolOutcome.Fail(callId, rejectionMessage);
                    }
                    else if (key == 'y' || key == 'Y')
                    {
                        break;
                    }
                    // Any other key: loop and ask again
                }
            }

            var result = await SearchWeb!.SearchAsync(query);

            // A missing backend is Success — it is a configuration state, not a failure.
            // Returning it as an error would feed ExitReasons.ToolErrorLimit and abort
            // precisely the runs where the model keeps trying to search.
            if (result.Success)
            {
                if (!NonInteractive)
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
            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return DenyByPolicy(callId, "fetch_url");
            if (NonInteractive)
                return DenyNonInteractive(callId, "fetch_url");

            // Show approval prompt — network fetches always require explicit approval
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Fetch:[/] {Markup.Escape(url)}");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: outbound network request[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    var reason = AnsiConsole.Prompt(
                        new Spectre.Console.TextPrompt<string>($"[{UIColors.SpectreMuted}]Reason (optional):[/]")
                            .DefaultValue("User declined")
                            .AllowEmpty());
                    var rejectionMessage = string.IsNullOrWhiteSpace(reason) || reason == "User declined"
                        ? "Error: User rejected fetch_url. Permission denied."
                        : $"Error: User rejected fetch_url. Reason: {reason}";
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Fetch rejected[/]");
                    return ToolOutcome.Fail(callId, rejectionMessage);
                }
                else if (key == 'y' || key == 'Y')
                {
                    break;
                }
                // Any other key: loop and ask again
            }

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
                        var msg = editResult.Replacements == 1
                            ? $"Successfully edited {editResult.Path} (1 replacement)"
                            : $"Successfully edited {editResult.Path} ({editResult.Replacements} replacements)";
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

            if (_approvalPolicy.Default == ApprovalDefault.Deny)
                return DenyByPolicy(callId, "edit_file (path outside working directory)");
            if (NonInteractive)
                return DenyNonInteractive(callId, "edit_file");

            // Show approval prompt with diff preview
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Edit:[/] {Markup.Escape(fullPath)}");

            var oldPreview = oldString.Length > 200 ? oldString[..200] + "..." : oldString;
            var newPreview = newString.Length > 200 ? newString[..200] + "..." : newString;
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]  - {Markup.Escape(oldPreview)}[/]");
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]  + {Markup.Escape(newPreview)}[/]");
            if (replaceAll)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]  (replace all occurrences)[/]");

            // Flush any pending input
            while (Console.KeyAvailable)
            {
                Console.ReadKey(intercept: true);
            }

            while (true)
            {
                AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
                var key = Console.ReadKey().KeyChar;
                Console.WriteLine();

                if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
                {
                    AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Edit rejected[/]");
                    return ToolOutcome.Fail(callId, "Error: User rejected file edit. Permission denied.");
                }
                else if (key == 'y' || key == 'Y')
                {
                    if (EditFile == null)
                        return ToolOutcome.Fail(callId, "Error: Edit file tool not initialized");

                    var result = EditFile.EditFile(path, oldString, newString, replaceAll);

                    if (result.Success)
                    {
                        Files.RecordWrite(result.Path);
                        var msg = result.Replacements == 1
                            ? $"Successfully edited {result.Path} (1 replacement)"
                            : $"Successfully edited {result.Path} ({result.Replacements} replacements)";
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(msg)}[/]");
                        return ToolOutcome.Ok(callId, msg);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]✗ {Markup.Escape(result.Error ?? "Unknown error")}[/]");
                        return ToolOutcome.Fail(callId, $"Error: {result.Error}");
                    }
                }
            }
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
            var summary = BuildApplySummary(preview);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(summary)}[/]");
            return ToolOutcome.Ok(callId, summary);
        }

        if (_approvalPolicy.Default == ApprovalDefault.Deny)
            return DenyByPolicy(callId, "apply_patch (path outside working directory)");
        if (NonInteractive)
            return DenyNonInteractive(callId, "apply_patch (path outside working directory)");

        // Flush any pending input
        while (Console.KeyAvailable) Console.ReadKey(intercept: true);

        while (true)
        {
            AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();

            if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Patch rejected[/]");
                return ToolOutcome.Fail(callId, "Error: User rejected apply_patch. Permission denied.");
            }
            else if (key == 'y' || key == 'Y')
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
                var summary = BuildApplySummary(preview);
                AnsiConsole.MarkupLine($"[{UIColors.SpectreSuccess}]✓[/] [{UIColors.SpectreMuted}]{Markup.Escape(summary)}[/]");
                return ToolOutcome.Ok(callId, summary);
            }
        }
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

    private static string BuildApplySummary(PatchPreview preview)
    {
        var added = preview.Files.Count(f => f.Kind == FileOpKind.Add);
        var updated = preview.Files.Count(f => f.Kind == FileOpKind.Update || f.Kind == FileOpKind.UpdateAndMove);
        var deleted = preview.Files.Count(f => f.Kind == FileOpKind.Delete);
        return $"Applied patch: {added} added, {updated} updated, {deleted} deleted";
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
    /// Checks if a path is within the trust sandbox (cwd/temp). If outside, prompts the user.
    /// Returns true if the access is approved, false if rejected.
    /// </summary>
    public bool ApproveReadPath(string toolLabel, string fullPath, string cwd)
    {
        var (trusted, symlinkEscape) = TrustSandbox.CheckPath(fullPath, cwd);
        var decision = _approvalPolicy.DecidePath(trusted && !symlinkEscape);
        if (decision == ApprovalDecision.Allow) return true;

        if (symlinkEscape)
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]⚠ Symlink escape:[/] [{UIColors.SpectreMuted}]{Markup.Escape(fullPath)} resolves outside working directory[/]");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]{toolLabel}:[/] {Markup.Escape(fullPath)}");
        AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]  Warning: path is outside working directory[/]");

        // No terminal to prompt at, or the policy default is deny: refuse the
        // out-of-sandbox access deterministically.
        if (decision == ApprovalDecision.Deny || NonInteractive)
        {
            Console.Error.WriteLine($"[nb] denied: {toolLabel} on a path outside the working directory ({(NonInteractive ? "non-interactive session" : "approval policy default is deny")}).");
            return false;
        }

        while (Console.KeyAvailable) Console.ReadKey(intercept: true);

        while (true)
        {
            AnsiConsole.Markup($"[{UIColors.SpectreUserPrompt}]Execute? [[y/N]][/] ");
            var key = Console.ReadKey().KeyChar;
            Console.WriteLine();
            if (key == 'n' || key == 'N' || key == '\r' || key == '\n')
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{toolLabel} rejected[/]");
                return false;
            }
            if (key == 'y' || key == 'Y') return true;
        }
    }

    /// <summary>
    /// Token counts for one model round-trip: the provider's own numbers when it reported
    /// them, a derived total when only the parts came back, and a size estimate when
    /// nothing did. Sets <see cref="UsageIsEstimated"/> (once, with a warning) on the
    /// estimate path.

    public static List<TodoChange> ParseTodoChanges(IDictionary<string, object?>? args)
    {
        var changes = new List<TodoChange>();
        if (args == null || !args.TryGetValue("changes", out var raw) || raw == null)
            return changes;

        try
        {
            var json = raw is JsonElement je ? je.GetRawText() : raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return changes;

            var parsed = JsonSerializer.Deserialize<List<TodoChange>>(json!, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed != null) changes.AddRange(parsed);
        }
        catch
        {
            // Leave changes empty; todo_write will report "no changes submitted"
        }
        return changes;
    }
}
