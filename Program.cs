using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Providers;
using nb.MCP;
using nb.Shell;
using nb.Transcript;
using nb.Utilities;
using UglyPrompt;

namespace nb;


public class Program
{
    // Static services used by the CLI shell. The engine itself is assembled by
    // nb.Core (NbRuntime / Nb.RunAsync); these cover the two shell-only paths:
    // --dump-tools (needs an McpManager) and --validate (needs the provider list).
    private static McpManager _mcpManager = new McpManager();
    private static ConfigurationService _configurationService = null!;
    private static ProviderManager _providerManager = new ProviderManager();

    private static LineEditor _lineEditor = CreateLineEditor();

    private static LineEditor CreateLineEditor()
    {
        var editor = new LineEditor();

        // File mentions (@trigger): word-start, indexed once from the launch
        // directory then filtered in memory. This is the `@file` include of the
        // source syntax, so it stays useful in the program REPL.
        editor.AddSource(FileMentionSource.Create(Directory.GetCurrentDirectory()));

        return editor;
    }

    private static bool _verbose = false;
    private static bool _dumpTools = false;
    private static bool _showHelp = false;
    private static string _outputMode = "interactive"; // interactive | porcelain | jsonl
    private static string? _seedFile = null;
    private static string? _configPath = null;
    private static string? _programFile = null;        // program path, "-" for stdin, or null (REPL)
    private static string? _mcpManifest = null;
    private static bool _validate = false;
    private static bool _resolve = false;

    private static string[] ParseFlags(string[] args)
    {
        var remainingArgs = new List<string>();

        // NB_OUTPUT seeds the flag default; an explicit --output below wins.
        var envOutput = Environment.GetEnvironmentVariable("NB_OUTPUT");
        if (!string.IsNullOrEmpty(envOutput))
        {
            _outputMode = envOutput.ToLowerInvariant();
            if (_outputMode is not ("interactive" or "jsonl" or "porcelain"))
            {
                Console.Error.WriteLine($"Error: unknown NB_OUTPUT mode '{_outputMode}'. Valid modes: interactive, porcelain, jsonl.");
                Environment.Exit(1);
            }
        }

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--verbose")
            {
                _verbose = true;
            }
            else if (args[i] == "--dump-tools")
            {
                _dumpTools = true;
            }
            else if (args[i] == "--output" && i + 1 < args.Length)
            {
                _outputMode = args[++i].ToLowerInvariant();
                if (_outputMode is not ("interactive" or "jsonl" or "porcelain"))
                {
                    Console.Error.WriteLine($"Error: unknown --output mode '{_outputMode}'. Valid modes: interactive, porcelain, jsonl.");
                    Environment.Exit(1);
                }
            }
            else if (args[i] == "--seed" && i + 1 < args.Length)
            {
                _seedFile = args[++i];
            }
            else if (args[i] == "--config" && i + 1 < args.Length)
            {
                _configPath = args[++i];
            }
            else if (args[i] == "--mcp" && i + 1 < args.Length)
            {
                _mcpManifest = args[++i];
            }
            else if (args[i] == "--validate")
            {
                _validate = true;
            }
            else if (args[i] == "--resolve")
            {
                _resolve = true;
            }
            else if (args[i] == "--help" || args[i] == "-h")
            {
                _showHelp = true;
            }
            else
            {
                remainingArgs.Add(args[i]);
            }
        }

        return remainingArgs.ToArray();
    }

    public static async Task Main(string[] args)
    {
        // Ctrl+C short-circuits our normal cleanup, so the spinner's cursor-hide
        // (`\x1b[?25l`) and bracketed-paste-enable (`\x1b[?2004h`) can bleed into
        // the parent shell. Restore both before the default handler kills us.
        Console.CancelKeyPress += (_, _) =>
        {
            try { Console.Write("\x1b[?25h\x1b[?2004l"); Console.Out.Flush(); } catch { }
        };

        var remainingArgs = ParseFlags(args);

        // The input is a program: a positional file, `-`, or piped stdin. With no
        // input and a TTY, we drop into the program REPL. nb is not a chat client:
        // there is no positional prompt.
        if (remainingArgs.Length > 1)
        {
            Console.Error.WriteLine("Error: expected at most one program (a file path or '-'). nb runs conversation-programs, not prompts.");
            Environment.Exit(1);
        }
        _programFile = remainingArgs.Length == 1 ? remainingArgs[0]
            : (Console.IsInputRedirected ? "-" : null);
        bool runRepl = _programFile == null && !_validate && !_resolve;

        // A program run is machine-oriented: default its output to jsonl (the
        // bytecode) so chrome relocates to stderr like the other machine modes.
        if (_programFile != null && _outputMode == "interactive")
            _outputMode = "jsonl";

        // Machine-output modes send all chrome (banners, streamed render, tool
        // noise) to stderr so stdout carries only the transcript. One seam: every
        // AnsiConsole.* call relocates with this.
        if (_outputMode is "jsonl" or "porcelain")
        {
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error),
            });
        }

        // Honor NO_COLOR (https://no-color.org). Spectre drops ANSI when output is
        // redirected but does not read NO_COLOR itself; applied after the jsonl swap.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (_showHelp)
        {
            PrintHelp();
            return;
        }

        // --dump-tools: connect to MCP servers, write manifest, exit.
        if (_dumpTools)
        {
            try { _mcpManager.LoadConfig(_mcpManifest); }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                Environment.Exit(1);
            }
            await _mcpManager.ConnectAllAsync();
            foreach (var (name, error) in _mcpManager.FailedServers)
                Console.Error.WriteLine($"MCP server '{name}' failed to start: {error}");
            var manifest = _mcpManager.BuildToolManifest();
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "mcp-tools.json");
            await File.WriteAllTextAsync(outputPath, json);
            Console.Error.WriteLine(outputPath);
            _mcpManager.Dispose();
            return;
        }

        // Build configuration — --config selects a hermetic single-file config,
        // otherwise the layered install/user/project resolution applies. A missing
        // --config file is a fatal config error.
        try
        {
            _configurationService = new ConfigurationService(_configPath);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: config file not found: {_configPath}");
            Environment.Exit(1);
        }

        var config = _configurationService.GetConfiguration();
        UIColors.LoadTheme();

        if (runRepl)
            await RunReplAsync(config);
        else
            await RunProgramAsync(config);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: nb [options] [program-file | -]");
        Console.WriteLine();
        Console.WriteLine("nb evaluates a conversation-program. Give a program file, or '-' / piped");
        Console.WriteLine("stdin to read one from stdin. With no input on a TTY, nb starts a REPL that");
        Console.WriteLine("interprets the same source syntax line by line.");
        Console.WriteLine();
        Console.WriteLine("Options (each varies how a program runs; it never replaces a program verb):");
        Console.WriteLine("  --help, -h              Show this help message");
        Console.WriteLine("  --output <mode>         jsonl (default for a program), porcelain, or interactive. jsonl/porcelain put the transcript on stdout, chrome on stderr");
        Console.WriteLine("  --seed <file>           Prepend a transcript (jsonl) as premise history before the program runs");
        Console.WriteLine("  --config <file>         Use this config file only (hermetic); default resolves install/user (~/.config/nb)/project (.nb/config.json) + NB_ env vars");
        Console.WriteLine("  --mcp <file>            Use this MCP manifest only (hermetic); default layers mcp.json across install/user/project");
        Console.WriteLine("  --validate              Parse and check the program, run nothing (exit 1 on error)");
        Console.WriteLine("  --resolve               Print the effective envelope at each run point, run nothing");
        Console.WriteLine("  --verbose               Verbose engine diagnostics (to stderr)");
        Console.WriteLine("  --dump-tools            Write the MCP tool manifest to mcp-tools.json and exit");
        Console.WriteLine();
        Console.WriteLine("Program verbs (source syntax): provider, model, mcp, tools, approval,");
        Console.WriteLine("loop, budget, system, user, assistant, run. See docs/conversation-program-cli.md.");
    }

    // The program REPL: interpret the same source syntax line by line. Each entered
    // line is parsed to directives and fed to one long-lived evaluator; a `run`
    // invokes the model and renders live. Not a chat client — no slash commands, no
    // persona. Ctrl-D (EOF) exits, exactly as a source program ends.
    private static async Task RunReplAsync(IConfiguration config)
    {
        NbRuntime runtime;
        try
        {
            runtime = await NbRuntime.BuildAsync(config, BuildNbOptions());
        }
        catch (NbStartupException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.Exit(1);
            return;
        }

        using (runtime)
        {
            await runtime.Mcp.ConnectAllAsync();
            foreach (var (name, error) in runtime.Mcp.FailedServers)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]MCP server '{name}' failed to start: {Markup.Escape(error)}[/]");
            var evaluator = new ProgramEvaluator(runtime.Conversation, runtime.ClientFactory);

            var mcpServers = runtime.Mcp.GetConnectedServerNames();
            var mcpList = mcpServers.Count > 0 ? string.Join(", ", mcpServers) : "none";
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]nb · {Markup.Escape(runtime.Conversation.GetCurrentProvider())} · mcp: {Markup.Escape(mcpList)} · enter program directives · Ctrl-D to exit[/]");

            bool bracketedPaste = !Console.IsInputRedirected;
            if (bracketedPaste) Console.Write("\x1b[?2004h");
            try
            {
                while (true)
                {
                    var line = _lineEditor.ReadLine($"[38;5;154m›[0m {UIColors.NativeUserInput}");
                    Console.Write(UIColors.NativeReset);
                    if (line == null) break;                       // EOF (Ctrl-D)
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    IReadOnlyList<TranscriptEvent> events;
                    try
                    {
                        events = ProgramParser.Parse(line, ResolveInclude);
                    }
                    catch (ProgramParseException ex)
                    {
                        Console.Error.WriteLine(ex.Message);
                        continue;
                    }

                    try
                    {
                        foreach (var ev in events)
                            await evaluator.EvaluateEventAsync(ev);
                    }
                    catch (Exception ex) when (ex is TranscriptFormatException or SandboxUnavailableException or McpServerUnavailableException)
                    {
                        Console.Error.WriteLine($"Error: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (bracketedPaste) Console.Write("\x1b[?2004l");
            }
        }
    }

    // Emit the transcript schema as JSONL on stdout (trailer inline). Chrome has
    // already been diverted to stderr (see the --output seam in Main).
    private static void EmitJsonl(IReadOnlyList<TranscriptEvent> events, ResultEvent trailer)
    {
        var all = new List<TranscriptEvent>(events) { trailer };
        Console.Out.Write(TranscriptSerializer.Serialize(all));
        Console.Out.Flush();
    }

    // Emit the same events as porcelain text on stdout; the run trailer goes to
    // stderr so stdout stays parseable (TOOL/RESULT lines + verbatim prose).
    private static void EmitPorcelain(IReadOnlyList<TranscriptEvent> events, ResultEvent trailer)
    {
        Console.Out.Write(TranscriptPorcelainWriter.Write(events));
        Console.Out.Flush();
        Console.Error.WriteLine(TranscriptPorcelainWriter.Trailer(trailer));
    }

    // Emit a facade RunResult in the resolved mode.
    private static void EmitResult(RunResult result, string mode)
    {
        var trailer = TranscriptMapper.ResultTrailer(result.Events, result.ExitReason, result.Usage);
        if (mode == "porcelain") EmitPorcelain(result.Events, trailer);
        else EmitJsonl(result.Events, trailer);
    }

    // Evaluate a conversation-program: read it (positional file / stdin), optionally
    // prepend a --seed transcript, detect source vs bytecode, then run it through the
    // library facade (which assembles its own engine, connects MCP, evaluates) and
    // emit. --validate / --resolve inspect without running.
    private static async Task RunProgramAsync(IConfiguration config)
    {
        var warnings = new List<string>();
        List<TranscriptEvent> program;
        try
        {
            program = await BuildProgramAsync(warnings);
        }
        catch (Exception ex) when (ex is TranscriptFormatException or ProgramParseException or FileNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        if (_validate) { ValidateProgram(program, config, warnings); return; }
        if (_resolve) { ResolveProgram(program); return; }

        // Refuse a directive nb cannot honour rather than dropping it and reporting that
        // at the end of the run: `--validate` already calls these errors, and the run
        // path disagreeing with it is what let an ignored `approval` value cost a whole
        // token budget before saying so.
        var shapeErrors = CheckDirectiveShape(program);
        if (shapeErrors.Count > 0)
        {
            foreach (var e in shapeErrors) Console.Error.WriteLine($"Error: {e}");
            Environment.ExitCode = 1;
            return;
        }

        RunResult result;
        try
        {
            result = await Nb.RunAsync(config, program, BuildNbOptions());
        }
        catch (Exception ex) when (ex is TranscriptFormatException or SandboxUnavailableException or NbStartupException or McpServerUnavailableException)
        {
            // Malformed fabricated tool round, an unhonorable `approval sandbox`, an
            // unassemblable engine, or a selected-but-dead MCP server — fail fast.
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        foreach (var w in warnings) Console.Error.WriteLine($"program: {w}");        // parse/seed warnings
        foreach (var w in result.Warnings) Console.Error.WriteLine($"program: {w}"); // evaluator warnings

        EmitResult(result, _outputMode);
        Environment.ExitCode = result.ExitCode;
    }

    // The per-invocation facade options carried from the CLI flags. Engine chrome goes
    // to stderr, matching the machine-output contract. Trust / no-tools / bash
    // auto-approve are program/config concerns now, not flags.
    private static NbOptions BuildNbOptions() => new()
    {
        Verbose = _verbose,
        McpManifestPath = _mcpManifest,
        DiagnosticsWriter = Console.Error,
    };

    // Assemble the program: an optional --seed premise prefix, then the body from the
    // positional file / stdin.
    private static async Task<List<TranscriptEvent>> BuildProgramAsync(IList<string> warnings)
    {
        var program = new List<TranscriptEvent>();

        if (_seedFile != null)
        {
            if (!File.Exists(_seedFile))
                throw new FileNotFoundException($"seed file not found: {_seedFile}");
            program.AddRange(TranscriptSerializer.Parse(await File.ReadAllTextAsync(_seedFile), warnings));
        }

        if (_programFile != null)
        {
            // Check first rather than letting File.ReadAllTextAsync decide: it raises
            // FileNotFoundException only when the directory exists, so a missing
            // directory (DirectoryNotFoundException) or a directory passed as the
            // program (UnauthorizedAccessException) would escape the caller's filter
            // and crash. Same shape as --seed above and --config.
            if (_programFile != "-" && !File.Exists(_programFile))
                throw new FileNotFoundException($"program file not found: {_programFile}");

            var source = _programFile == "-"
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(_programFile);
            program.AddRange(ParseProgramSource(source, warnings));
        }

        return program;
    }

    // --validate: parse succeeded (we got here); report structural warnings and
    // semantic errors (unknown provider, bad approval directive), run nothing. Exit 1
    // on any error, 0 otherwise.
    private static void ValidateProgram(IReadOnlyList<TranscriptEvent> program, IConfiguration config, IList<string> warnings)
    {
        var providers = _providerManager.GetConfiguredProviders(config)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var ev in program)
        {
            if (ev is ProviderEvent p2 && providers.Count > 0 && !providers.Contains(p2.Name))
                errors.Add($"unknown provider '{p2.Name}'. Configured: {string.Join(", ", providers)}.");
        }

        errors.AddRange(CheckDirectiveShape(program));

        foreach (var w in warnings) Console.Error.WriteLine($"warning: {w}");
        foreach (var e in errors) Console.Error.WriteLine($"error: {e}");

        if (errors.Count > 0)
        {
            Console.Error.WriteLine($"invalid: {errors.Count} error(s).");
            Environment.ExitCode = 1;
        }
        else
        {
            Console.Error.WriteLine($"valid: {program.Count} directive(s).");
        }
    }

    // The config-free half of validation: directives whose value nb cannot honour. The
    // run path checks these too, before spending a token — an unhonorable directive was
    // otherwise dropped silently and only reported in the warning drain *after* the run,
    // which for `approval` means a safety directive going missing for the whole run.
    internal static List<string> CheckDirectiveShape(IReadOnlyList<TranscriptEvent> program)
    {
        var errors = new List<string>();

        foreach (var ev in program)
        {
            switch (ev)
            {
                case ApprovalEvent a when a.Key is not ("bash" or "mcp" or "search" or "default" or "sandbox"):
                    errors.Add($"invalid approval key '{a.Key}'. Valid: bash, mcp, search, default, sandbox.");
                    break;
                case ApprovalEvent { Key: "default" } a when a.Value is not ("prompt" or "deny"):
                    errors.Add($"invalid approval default '{a.Value}'. Valid: prompt, deny.");
                    break;
                case ApprovalEvent { Key: "search" } a when a.Value is not ("allow" or "prompt"):
                    errors.Add($"invalid approval search '{a.Value}'. Valid: allow, prompt.");
                    break;
                case ApprovalEvent { Key: "sandbox" } a when !BwrapSandbox.TryParse(a.Value, out _, out _):
                    errors.Add($"invalid approval sandbox '{a.Value}'. Valid: none, bwrap, bwrap-net.");
                    break;
                case LoopEvent { Enabled: true } l when l.Threshold < 2:
                    errors.Add($"invalid loop threshold '{l.Threshold}'. Use an integer >= 2, or 'loop off'.");
                    break;
                case BudgetEvent b when b.Key is not ("tokens" or "tool_calls" or "wall_ms"):
                    errors.Add($"invalid budget key '{b.Key}'. Valid: tokens, tool_calls, wall_ms.");
                    break;
                case BudgetEvent b when b.Value <= 0:
                    errors.Add($"invalid budget value '{b.Value}' for '{b.Key}'. Use a positive integer.");
                    break;
            }
        }

        return errors;
    }

    // --resolve: walk the directives without invoking, printing the effective
    // envelope at each run point (the ordering inspector for anywhere-config).
    private static void ResolveProgram(IReadOnlyList<TranscriptEvent> program)
    {
        string provider = "(default)", model = "(default)", output = _outputMode;
        var surfaceDirectives = new List<SurfaceDirectiveEvent>();
        string approvalDefault = "prompt", sandbox = "none";
        int bashRules = 0, mcpRules = 0;
        string loop = "default";
        long? tokenBudget = null, toolCallBudget = null, wallBudget = null;
        int run = 0;

        foreach (var ev in program)
        {
            switch (ev)
            {
                case ProviderEvent p: provider = p.Name; break;
                case ModelEvent m: model = m.Name; break;
                case SurfaceDirectiveEvent sd: surfaceDirectives.Add(sd); break;
                case ApprovalEvent { Key: "default" } a: approvalDefault = a.Value; break;
                case ApprovalEvent { Key: "sandbox" } a: sandbox = a.Value; break;
                case ApprovalEvent { Key: "bash" }: bashRules++; break;
                case ApprovalEvent { Key: "mcp" }: mcpRules++; break;
                case LoopEvent l: loop = l.Enabled ? $"on({l.Threshold})" : "off"; break;
                case BudgetEvent { Key: "tokens" } b: tokenBudget = b.Value; break;
                case BudgetEvent { Key: "tool_calls" } b: toolCallBudget = b.Value; break;
                case BudgetEvent { Key: "wall_ms" } b: wallBudget = b.Value; break;
                case RunEvent:
                    run++;
                    // Fold through the same resolver the evaluator runs, so what this
                    // prints is provably what a run exposes (plans/tool-surface-directives.md).
                    var surface = ToolSurface.Fold(surfaceDirectives, ConversationManager.NativeToolNames);
                    var mcpStr = surface.McpServers is { Count: > 0 } s ? string.Join(",", s) : "(none)";
                    var toolStr = surface.NativeAllow is null
                        ? "all"
                        : surface.NativeAllow.Count > 0 ? string.Join(",", surface.NativeAllow.OrderBy(n => n)) : "(none)";
                    var budgetStr = $"tokens:{(tokenBudget?.ToString() ?? "-")} tool_calls:{(toolCallBudget?.ToString() ?? "-")} wall_ms:{(wallBudget?.ToString() ?? "-")}";
                    Console.WriteLine($"run {run}: provider={provider} model={model} output={output} mcp=[{mcpStr}] tools={toolStr} approval={approvalDefault}(bash:{bashRules} mcp:{mcpRules}) sandbox={sandbox} loop={loop} budget=[{budgetStr}]");
                    break;
            }
        }

        if (run == 0)
            Console.WriteLine($"no runs. provider={provider} model={model} output={output}");
    }

    // First non-blank, non-comment line starting with '{' => JSONL bytecode.
    private static bool LooksLikeJsonl(string source)
    {
        foreach (var line in source.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("#")) continue;
            return t.StartsWith("{");
        }
        return false;
    }

    private static IReadOnlyList<TranscriptEvent> ParseProgramSource(string source, IList<string> warnings) =>
        LooksLikeJsonl(source)
            ? TranscriptSerializer.Parse(source, warnings)
            : ProgramParser.Parse(source, ResolveInclude);

    // Resolve an @file include for the source parser: relative to the program
    // file's directory (or cwd for a stdin/REPL program), fail fast if missing.
    private static string ResolveInclude(string relPath)
    {
        var baseDir = _programFile is not null and not "-"
            ? Path.GetDirectoryName(Path.GetFullPath(_programFile)) ?? "."
            : Directory.GetCurrentDirectory();
        var full = Path.IsPathRooted(relPath) ? relPath : Path.Combine(baseDir, relPath);
        if (!File.Exists(full))
            throw new ProgramParseException($"@include not found: {relPath}");
        return File.ReadAllText(full);
    }
}
