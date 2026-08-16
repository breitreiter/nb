using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Shell.ApplyPatch;
using nb.Transcript;

namespace nb.Harness;

/// <summary>
/// The Codex CLI surface — and the costume that shows how little of a harness is the
/// tool <i>list</i>.
///
/// Codex has no read, write, edit, glob, grep or list tool. It has a shell, a patch
/// applier, a plan tracker and an image viewer, and everything else is a shell command:
/// the model greps with <c>rg</c> and reads with <c>sed -n</c>. Reproducing that means
/// *withholding* seven tools nb has and the model would otherwise reach for, which is a
/// larger behavioural change than any renaming the qwen costume does.
///
/// Names and schemas verified against <c>openai/codex</c> (Apache-2.0) at
/// <c>codex-rs/core/src/tools/handlers/*_spec.rs</c>. The preamble is that project's
/// own prompt, vendored — see <c>prompts/harness/codex.md</c>.
/// </summary>
public sealed class CodexHarness : NbHarness
{
    public const string HarnessName = "codex";

    public CodexHarness(
        BashTool? bash = null, ReadFileTool? readFile = null, WriteFileTool? writeFile = null,
        EditFileTool? editFile = null, FindFilesTool? findFiles = null, GrepTool? grep = null,
        ListDirTool? listDir = null, FetchUrlTool? fetchUrl = null, SearchWebTool? searchWeb = null,
        ApplyPatchTool? applyPatch = null)
        : base(bash, readFile, writeFile, editFile, findFiles, grep, listDir, fetchUrl, searchWeb, applyPatch)
    {
        // Codex has no read tool, so nb's read-before-edit rule is unsatisfiable here:
        // the model reads through the shell, which the tracker cannot observe, and every
        // apply_patch would be refused for a file it had just read with `sed`. Turning it
        // off is what makes the costume usable, and it is what the real harness does.
        // The trust sandbox and approval policy are untouched.
        Files.RequireReadBeforeEdit = false;
    }

    /// <summary>Wear Codex's surface over an existing harness's tools.</summary>
    public CodexHarness(NbHarness source) : base(source)
    {
        // Same reason as the constructor above.
        Files.RequireReadBeforeEdit = false;
    }

    public override string Name => HarnessName;

    public override string? Preamble => _preamble;

    private static readonly string? _preamble = LoadPreamble("codex.md");

    /// <summary>Codex's total budget for project docs, across every file it collects.</summary>
    private const int AgentsMdMaxBytes = 32 * 1024;

    /// <summary>
    /// AGENTS.md, in Codex's own wrapper. Codex collects them from the project root down
    /// to the cwd and hands the model the concatenation inside an <c>&lt;INSTRUCTIONS&gt;</c>
    /// block headed by the directory — verified against <c>codex-rs/core/src/agents_md.rs</c>
    /// and <c>context/user_instructions.rs</c>. The wrapper is not decoration: it is what
    /// tells the model these are workspace instructions rather than part of the task.
    /// </summary>
    public override string? ProjectInstructions()
    {
        var cwd = WorkingDirectory;

        // AGENTS.override.md wins over AGENTS.md within a directory, per candidate_filenames.
        var docs = DiscoverProjectDocs(cwd, AgentsMdMaxBytes, "AGENTS.override.md", "AGENTS.md");
        if (docs.Count == 0) return null;

        var body = string.Join("\n\n", docs.Select(d => d.Text.TrimEnd()));
        return $"# AGENTS.md instructions for {cwd}\n\n<INSTRUCTIONS>\n{body}\n</INSTRUCTIONS>";
    }

    /// <summary>
    /// Codex's <c>&lt;environment_context&gt;</c> block, verified against the render in
    /// <c>codex-rs/core/src/context/world_state/environment.rs</c> and its snapshot test.
    ///
    /// One available environment takes the flat "legacy single" layout — <c>cwd</c> and
    /// <c>shell</c> inline rather than wrapped in <c>&lt;environments&gt;</c> — which is
    /// nb's case exactly: one local shell, always available. Element order is the render
    /// function's own.
    ///
    /// The <c>network</c> and <c>filesystem</c> elements are not emitted; see the
    /// omissions.
    /// </summary>
    public override string? EnvironmentContext()
    {
        var now = DateTime.Now;
        var lines = new List<string>
        {
            $"  <cwd>{Escape(WorkingDirectory)}</cwd>",
        };

        if (Bash?.Environment.ShellName is { Length: > 0 } shell)
            lines.Add($"  <shell>{Escape(shell)}</shell>");

        lines.Add($"  <current_date>{now:yyyy-MM-dd}</current_date>");
        lines.Add($"  <timezone>{Escape(TimeZoneInfo.Local.Id)}</timezone>");

        return $"<environment_context>\n{string.Join("\n", lines)}\n</environment_context>";

        static string Escape(string value) => value
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");
    }

    public override IReadOnlyList<string> Omissions
    {
        get
        {
            var omissions = new List<string>
            {
                _preamble is null
                    ? "system prompt: MISSING — prompts/harness/codex.md did not load, so this run is tool-surface-only."
                    : "system prompt: Codex's own text, vendored under Apache-2.0, with two stated modifications — the model identity is generalised, and the sentence declaring apply_patch a freeform tool is dropped because nb declares it as a JSON function. See the header of prompts/harness/codex.md.",
            };
            omissions.AddRange(_omissions);
            return omissions;
        }
    }

    private static readonly string[] _omissions = new[]
    {
        "apply_patch: declared as a JSON function taking an input string. Codex declares it as a freeform tool with a lark grammar, so the model emits a bare patch rather than an argument object. nb has no freeform tool channel. The patch format itself is identical.",
        "AGENTS.md: loaded from the project root down to the cwd, in Codex's own <INSTRUCTIONS> wrapper, but sent as a system message where Codex sends a user-role fragment. Not reproduced: user-level instructions from ~/.codex, and Codex's re-check when the model works outside the cwd — its prompt tells the model to look for those itself, which this costume's shell can do.",
        "environment context: the <environment_context> block carries cwd, shell, current_date and timezone in Codex's verified layout, but not its <network> or <filesystem> elements — mapping nb's approval model and trust sandbox onto Codex's permission-profile vocabulary would mean inventing enum spellings, which is worse than a missing element. Approval-policy instructions are not appended to the prompt either, so the prompt's references to a \"Sandbox and approvals\" section still point at nothing.",
        "escalated execution: refusals are shaped as Codex's escalatable sandbox failure (verified from codex-rs/core/src/tools/runtimes/shell/unix_escalation.rs), but shell_command does not declare with_escalated_permissions, so a model cannot actually request escalation the way it can under Codex. nb has no mid-run elevation to grant: authorization is fixed by the program before the run. The refusal names the approval directive that would have allowed it instead — a real remedy for the operator rather than a parameter the model would emit and have rejected.",
        "shell_command: workdir and timeout_ms are accepted and ignored — nb's bash runs in the shell cwd on its own configured timeout.",
        "view_image: detail is accepted and ignored. A path that is not an image comes back as text rather than as an error.",
        "update_plan: mapped onto nb's todo list. Codex replaces the plan wholesale; this reproduces that by cancelling steps the new plan drops, but nb renders the list its own way rather than as Codex's plan widget.",
        "surface size: Codex also ships unified exec (exec_command/write_stdin), web_search, MCP resource tools, sub-agents, skills and plugins, most behind feature flags. This costume covers the default four.",
    };

    public override IReadOnlyList<AIFunction> CreateTools(ToolSurface surface)
    {
        var tools = new List<AIFunction>();

        if (Bash != null && surface.AllowsNative("bash"))
            tools.Add(Declare("shell_command",
                "Runs a shell command and returns its output.\n"
                + "- Always set the `workdir` param when using the shell_command function. Do not use `cd` unless absolutely necessary.",
                new SchemaBuilder()
                    .Add("command", "string", "Shell script to run in the user's default shell.", required: true)
                    .Add("workdir", "string", "Working directory for the command. Defaults to the turn cwd.")
                    .Add("timeout_ms", "number", "Maximum command runtime. Defaults to 10000 ms.")));

        // Codex reaches files through the shell and edits them through apply_patch. The
        // absence of read_file / write_file / edit_file / find_files / grep / list_dir is
        // the costume, not an oversight: those tools exist on this harness and are
        // deliberately not advertised.
        if (ApplyPatch != null && surface.AllowsNative("apply_patch"))
            tools.Add(Declare("apply_patch",
                "The `apply_patch` tool can be used to edit files.",
                new SchemaBuilder()
                    .Add("input", "string", "A complete patch, beginning with *** Begin Patch and ending with *** End Patch.", required: true)));

        if (surface.AllowsNative("todo"))
            tools.Add(Declare("update_plan",
                "Updates the task plan.\nProvide an optional explanation and a list of plan items, each with a step and status.\nAt most one step can be in_progress at a time.",
                new SchemaBuilder()
                    .Add("explanation", "string", "Optional explanation for this plan update.")
                    .AddArray("plan", "The list of steps", required: true, item: new SchemaBuilder()
                        .Add("step", "string", "Task step text.", required: true)
                        .AddEnum("status", new[] { "pending", "in_progress", "completed" }, "Step status.", required: true))));

        if (ReadFile != null && surface.AllowsNative("read_file"))
            tools.Add(Declare("view_image",
                "View a local image file from the filesystem when visual inspection is needed. Use this for images already available on disk.",
                new SchemaBuilder()
                    .Add("path", "string", "Local filesystem path to an image file.", required: true)
                    .AddEnum("detail", new[] { "high", "original" },
                        "Image detail level. Defaults to `high`; use `original` to preserve exact resolution.")));

        return tools;
    }

    private static AIFunction Declare(string name, string description, SchemaBuilder schema) =>
        new DeclaredFunction(name, description, schema.Build());

    // ---- Result formatting ------------------------------------------------------
    // Both shapes are verified against openai/codex rather than reconstructed:
    // format_exec_output_for_model in codex-rs/core/src/tools/mod.rs, and print_summary
    // in codex-rs/apply-patch/src/lib.rs.

    /// <summary>
    /// Codex leads with the exit code and labels the body, where nb trails an
    /// <c>[exit code: N]</c> footer. The difference is not cosmetic: the model reads the
    /// status before the output rather than after it.
    /// </summary>
    protected override string FormatShellResult(ShellResult result)
    {
        var body = result.TimedOut
            ? $"command timed out\n{Combined(result)}"
            : Combined(result);

        var sections = new List<string> { $"Exit code: {result.ExitCode}" };
        if (result.Truncated) sections.Add("Total output lines: (truncated)");
        sections.Add("Output:");
        sections.Add(body);

        return string.Join("\n", sections);

        // Codex's shell aggregates the two streams into one, unlabelled — there is no
        // [stderr] marker to key off.
        static string Combined(ShellResult r) =>
            string.Concat(r.Stdout, r.Stderr).TrimEnd();
    }

    /// <summary>Codex's git-style patch summary: a status letter and a path per file.</summary>
    protected override string FormatApplyPatchResult(PatchPreview preview)
    {
        var lines = new List<string> { "Success. Updated the following files:" };
        lines.AddRange(Paths(FileOpKind.Add).Select(p => $"A {p}"));
        lines.AddRange(preview.Files
            .Where(f => f.Kind is FileOpKind.Update or FileOpKind.UpdateAndMove)
            .Select(f => $"M {f.FinalPath}"));
        lines.AddRange(Paths(FileOpKind.Delete).Select(p => $"D {p}"));
        return string.Join("\n", lines);

        IEnumerable<string> Paths(FileOpKind kind) =>
            preview.Files.Where(f => f.Kind == kind).Select(f => f.FinalPath);
    }

    /// <summary>
    /// **escalatable** — verified. Codex surfaces a blocked command as a sandbox failure
    /// offering a retry (`command failed; retry without sandbox?`), and the model re-issues
    /// the call asking for elevated permission
    /// (`codex-rs/core/src/tools/orchestrator.rs`,
    /// `codex-rs/core/src/tools/runtimes/shell/unix_escalation.rs`; issues #19162, #18079).
    /// The retry is a real move there, which makes this a distinct transcript shape: the
    /// model spends turns escalating rather than routing around.
    ///
    /// **What is deliberately not reproduced: the escalation parameter.** Codex's retry
    /// rides `with_escalated_permissions=true` on the tool call, and this costume's
    /// `shell_command` schema does not declare it — so instructing the model to set it
    /// would manufacture a malformed call. That is the same defect as naming an approval
    /// directive that does not exist, which step 2 of the prompt-removal plan existed to
    /// fix. So the refusal carries the escalation *shape* (a sandbox-denied failure the
    /// model may reasonably retry against) without naming a parameter it cannot send.
    /// Under `approval_policy=never` real Codex also just fails back to the model, which
    /// is the case this most resembles.
    /// </summary>
    protected override string RefusalText(string tool, string rung, string remedy) =>
        $"{tool} — command failed in sandbox: {Because(rung)}. " +
        (IsDirective(remedy)
            ? $"Escalated execution requires this directive in the program: {remedy}."
            : remedy);

    protected override async Task<ToolOutcome?> DispatchAsync(
        string name, string callId, IDictionary<string, object?>? arguments, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case "shell_command" when Bash != null:
                // workdir and timeout_ms are accepted and ignored. Codex has no
                // description argument — it narrates in prose before the call instead —
                // so there is nothing to show above an approval prompt but the command.
                return await HandleBashToolCall(callId, Str(arguments, "command"), "");

            case "apply_patch" when ApplyPatch != null:
                return HandleApplyPatchToolCall(callId, Str(arguments, "input"));

            case "update_plan":
                // update_plan replaces the plan; nb's todo list merges. The base class
                // owns the conversion, since Claude Code's TodoWrite needs the same one.
                return HandleTodoWriteToolCall(callId,
                    AsWholeListReplacement(ParseChecklist(arguments, "plan", "step")));

            case "view_image" when ReadFile != null:
                return HandleReadFileToolCall(callId, Str(arguments, "path"), null, null);

            default:
                return null;
        }
    }

}
