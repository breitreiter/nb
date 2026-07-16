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
    private static IChatClient _client = null!;
    private static McpManager _mcpManager = new McpManager();
    private static FakeToolManager _fakeToolManager = new FakeToolManager();
    private static ConversationManager _conversationManager = null!;
    private static ConfigurationService _configurationService = null!;
    private static ProviderManager _providerManager = new ProviderManager();
    private static CommandProcessor _commandProcessor = null!;
    private static ShellEnvironment _shellEnvironment = null!;
    private static BashTool _bashTool = null!;
    private static ReadFileTool _readFileTool = null!;
    private static WriteFileTool _writeFileTool = null!;
    private static EditFileTool _editFileTool = null!;
    private static FindFilesTool _findFilesTool = null!;
    private static GrepTool _grepTool = null!;
    private static ListDirTool _listDirTool = null!;
    private static FetchUrlTool _fetchUrlTool = null!;
    private static ApplyPatchTool _applyPatchTool = null!;
    private static ApprovalPatterns _approvalPatterns = new ApprovalPatterns();
    private static readonly List<CompletionHint> _commandHints = new()
    {
        new("//", "Back"),
        new("/clear", "Clear conversation"),
        new("/edit", "Compose in $EDITOR"),
        new("/provider", "Switch AI provider"),
        new("/tools", "List available tools and approval status"),
        new("/quit", "Quit"),
    };

    private static LineEditor _lineEditor = CreateLineEditor();

    private static LineEditor CreateLineEditor()
    {
        var editor = new LineEditor();

        // Slash-commands: line-start trigger, filtered against the static list.
        editor.AddSource(new CompletionSource('/', TriggerAnchor.LineStart,
            body => _commandHints
                .Where(c => c.Name.StartsWith("/" + body, StringComparison.OrdinalIgnoreCase))
                .ToList()));

        // File mentions (@trigger): word-start, indexed once from the launch
        // directory then filtered in memory.
        editor.AddSource(FileMentionSource.Create(Directory.GetCurrentDirectory()));

        return editor;
    }

    private static string? _systemPromptOverride = null;
    private static bool _noBash = false;
    private static bool _verbose = false;
    private static bool _dumpTools = false;
    private static bool _showHelp = false;
    private static bool _trustMode = false;
    private static bool _debugStream = false;
    private static string _outputMode = "interactive"; // interactive | porcelain | jsonl
    private static string? _seedFile = null;
    private static string? _configPath = null;
    private static string? _programFile = null;
    private static string? _specName = null;
    private static bool _validate = false;
    private static bool _resolve = false;
    private static string? _mcpManifest = null;

    private static string BuildUserInput(string[] args, string? stdinContent)
    {
        // Treat piped stdin as content to include in the message
        // Args (if present) become the instruction for what to do with the content

        if (!string.IsNullOrWhiteSpace(stdinContent))
        {
            var content = stdinContent.TrimEnd();

            // If we have args, format as: <content>\n\n<instruction>
            if (args.Length > 0)
            {
                var instruction = string.Join(" ", args);
                return $"```\n{content}\n```\n\n{instruction}";
            }
            // If no args, just send the content wrapped in code block
            else
            {
                return $"```\n{content}\n```";
            }
        }
        else
        {
            // No stdin, just use args as normal
            return string.Join(" ", args);
        }
    }

    private static string LoadSystemPrompt()
    {
        // If --system flag was provided, load from that path
        if (!string.IsNullOrEmpty(_systemPromptOverride))
        {
            if (!File.Exists(_systemPromptOverride))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Error: System prompt file not found: {Markup.Escape(_systemPromptOverride)}[/]");
                Environment.Exit(1);
            }
            return File.ReadAllText(_systemPromptOverride);
        }

        // Otherwise use default from ConfigurationService
        return _configurationService.GetSystemPrompt();
    }

    private static string? LoadProjectContext(string cwd)
    {
        // Look for NB.md in cwd, then walk up to find it in parent dirs
        var dir = Path.GetFullPath(cwd);
        string? nbMdPath = null;

        while (dir != null)
        {
            var candidate = Path.Combine(dir, "NB.md");
            if (File.Exists(candidate))
            {
                nbMdPath = candidate;
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        var sections = new List<string>();

        if (nbMdPath != null)
        {
            var content = File.ReadAllText(nbMdPath).Trim();
            if (!string.IsNullOrEmpty(content))
            {
                AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Loaded project context: {Markup.Escape(nbMdPath)}[/]");
                sections.Add($"## Project Context (NB.md)\n\n{content}");
            }
        }

        // Check for other vendor context files — don't auto-load, just tell the model they exist
        var contextFiles = new[] { "CLAUDE.md", "AGENTS.md", "COPILOT.md", "CURSORRULES", ".cursorrules", "GEMINI.md", ".github/copilot-instructions.md" };
        var found = contextFiles
            .Select(f => Path.Combine(Path.GetFullPath(cwd), f))
            .Where(File.Exists)
            .Select(f => Path.GetFileName(f))
            .ToList();

        if (found.Count > 0)
        {
            sections.Add($"## Other Context Files\n\nThe following project context files exist in the working directory: {string.Join(", ", found)}. Use `read_file` to read them if they seem relevant to the user's request.");
        }

        return sections.Count > 0 ? string.Join("\n\n", sections) : null;
    }

    private static float? ResolveProviderFloat(Microsoft.Extensions.Configuration.IConfiguration config, string providerName, string key)
    {
        foreach (var provider in config.GetSection("ChatProviders").GetChildren())
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
                return float.TryParse(provider[key], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return null;
    }

    private static int ResolveMaxContextTokens(Microsoft.Extensions.Configuration.IConfiguration config, string providerName)
    {
        var providers = config.GetSection("ChatProviders").GetChildren();
        foreach (var provider in providers)
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
            {
                if (int.TryParse(provider["MaxContextTokens"], out var providerTokens))
                    return providerTokens;
                break;
            }
        }
        return int.TryParse(config["MaxContextTokens"], out var tokens) ? tokens : 128000;
    }

    private static string? ResolveActiveModelSlug(Microsoft.Extensions.Configuration.IConfiguration config, string providerName)
    {
        var providers = config.GetSection("ChatProviders").GetChildren();
        foreach (var provider in providers)
        {
            if (provider["Name"]?.Equals(providerName, StringComparison.OrdinalIgnoreCase) == true)
            {
                var model = provider["Model"] ?? provider["ChatDeploymentName"];
                return string.IsNullOrEmpty(model) ? null : SlugifyModelName(model);
            }
        }
        return null;
    }

    // Build the approval policy from --trust/--approve plus the Approval config
    // block. Approval.Bash extends the --approve bash patterns; Approval.McpTools
    // are allow globs (alongside mcp.json alwaysAllow); Approval.Default=deny makes
    // unmatched calls deny instead of prompt. See plans/approval-policy-and-sandbox.md.
    private static ApprovalPolicy BuildApprovalPolicy(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        foreach (var entry in config.GetSection("Approval:Bash").GetChildren())
            if (!string.IsNullOrEmpty(entry.Value)) _approvalPatterns.Add(entry.Value);

        var mcpGlobs = config.GetSection("Approval:McpTools").GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!);

        var def = string.Equals(config["Approval:Default"], "deny", StringComparison.OrdinalIgnoreCase)
            ? ApprovalDefault.Deny : ApprovalDefault.Prompt;

        var policy = new ApprovalPolicy(_trustMode, _approvalPatterns, _mcpManager.IsAlwaysAllowed, mcpGlobs, def);

        var sandboxValue = config["Approval:Sandbox"];
        if (!string.IsNullOrEmpty(sandboxValue))
        {
            if (!BwrapSandbox.TryParse(sandboxValue, out var mode, out var allowNet))
            {
                Console.Error.WriteLine($"Error: invalid Approval.Sandbox '{sandboxValue}'. Valid: none, bwrap, bwrap-net.");
                Environment.Exit(1);
            }
            RequireSandboxAvailable(mode);
            policy.SetSandbox(mode, allowNet);
        }

        return policy;
    }

    // Probe for the sandbox and hard-fail if it was requested but the host can't honor
    // it (non-Linux / bwrap not on PATH). Inspection modes never touch the environment.
    private static void RequireSandboxAvailable(SandboxMode mode)
    {
        if (mode != SandboxMode.Bwrap || _validate || _resolve) return;
        if (!BwrapSandbox.IsAvailable())
        {
            Console.Error.WriteLine("Error: Sandbox 'bwrap' requested but bubblewrap (bwrap) is not available on this host (Linux + bwrap on PATH required).");
            Environment.Exit(1);
        }
    }

    private static string SlugifyModelName(string model)
    {
        // Lowercase + collapse non-alphanumeric runs to '-'. Keeps "gpt-oss-20b" as-is,
        // turns "meta-llama/Llama-3.1-8B" into "meta-llama-llama-3-1-8b".
        var lower = model.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(lower.Length);
        var lastWasDash = false;
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        return sb.ToString().Trim('-');
    }

    private static string[] ParseFlags(string[] args)
    {
        var remainingArgs = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--approve" && i + 1 < args.Length)
            {
                _approvalPatterns.Add(args[++i]);
            }
            else if (args[i] == "--system" && i + 1 < args.Length)
            {
                _systemPromptOverride = args[++i];
            }
            else if (args[i] == "--nobash")
            {
                _noBash = true;
            }
            else if (args[i] == "--verbose")
            {
                _verbose = true;
            }
            else if (args[i] == "--trust")
            {
                _trustMode = true;
            }
            else if (args[i] == "--dump-tools")
            {
                _dumpTools = true;
            }
            else if (args[i] == "--debug-stream")
            {
                _debugStream = true;
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
            else if (args[i] == "--program" && i + 1 < args.Length)
            {
                _programFile = args[++i];
            }
            else if (args[i] == "--spec" && i + 1 < args.Length)
            {
                _specName = args[++i];
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

        // Parse flags (--approve, --system) before processing other args
        var remainingArgs = ParseFlags(args);

        // A program run is machine-oriented: default its output to jsonl (the
        // bytecode) so chrome relocates to stderr like the other machine modes.
        // An `output` directive in the program can still override the final emit.
        if ((_programFile != null || _specName != null) && _outputMode == "interactive")
            _outputMode = "jsonl";

        // Machine-output modes send all chrome (banners, streamed render, tool
        // noise, approval prompts) to stderr so stdout carries only the
        // transcript. One seam: every AnsiConsole.* call relocates with this.
        if (_outputMode is "jsonl" or "porcelain")
        {
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(Console.Error),
            });
        }

        // Honor NO_COLOR (https://no-color.org): any non-empty value disables
        // color. Spectre already drops ANSI when output is redirected, but it
        // does not read NO_COLOR itself, so a user on a TTY who sets it still
        // gets color without this. Applied after the jsonl swap so it gates
        // whichever console ends up active.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            AnsiConsole.Profile.Capabilities.ColorSystem = ColorSystem.NoColors;

        if (_showHelp)
        {
            Console.WriteLine("Usage: nb [options] [prompt]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --help, -h              Show this help message");
            Console.WriteLine("  --system <file>         Load system prompt from file");
            Console.WriteLine("  --approve <pattern>     Pre-approve shell commands matching pattern");
            Console.WriteLine("  --trust                 Auto-approve file tools and safe bash commands within cwd");
            Console.WriteLine("  --nobash                Disable shell tool");
            Console.WriteLine("  --verbose               Enable verbose output");
            Console.WriteLine("  --dump-tools            Write MCP tool manifest to mcp-tools.json and exit");
            Console.WriteLine("  --mcp <file>            Load MCP servers from this manifest instead of mcp.json; their tools are exposed to the model");
            Console.WriteLine("  --debug-stream          Always dump streaming response telemetry to .nb_turn_dumps/");
            Console.WriteLine("  --output <mode>         Output mode: interactive (default), porcelain (TOOL/RESULT lines + verbatim answer), or jsonl (typed transcript). porcelain/jsonl put the answer on stdout, chrome on stderr");
            Console.WriteLine("  --seed <file>           Load a transcript (jsonl) as premise history before the prompt runs");
            Console.WriteLine("  --config <file>         Use this config file only (hermetic); default resolves install/user (~/.config/nb)/project (.nb/config.json) + NB_ env vars");
            Console.WriteLine("  --program <file>        Evaluate a conversation-program (source syntax or jsonl bytecode; '-' for stdin). No default persona; provider/model may switch between runs");
            Console.WriteLine("  --spec <name|file>      Prepend a reusable directive prefix (built-in 'chat' = the default persona, 'headless' = jsonl output; a path; or specs/<name>.nb in .nb/ or ~/.config/nb). Composes with --program or a prompt arg");
            Console.WriteLine("  --validate              Parse and check the program/spec, run nothing (exit 1 on error)");
            Console.WriteLine("  --resolve               Print the effective envelope at each run point, run nothing");
            Console.WriteLine();
            Console.WriteLine("With no arguments, starts interactive mode.");
            Console.WriteLine("With a prompt argument, runs in single-shot mode.");
            Console.WriteLine("Stdin is accepted and included as context.");
            return;
        }

        // --dump-tools: connect to MCP servers, write manifest, exit
        if (_dumpTools)
        {
            _mcpManager.LoadConfig(_mcpManifest);
            await _mcpManager.ConnectAllAsync();
            var manifest = _mcpManager.BuildToolManifest();
            var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            var outputPath = Path.Combine(Directory.GetCurrentDirectory(), "mcp-tools.json");
            await File.WriteAllTextAsync(outputPath, json);
            Console.Error.WriteLine(outputPath);
            _mcpManager.Dispose();
            return;
        }

        // Build configuration now that flags are parsed — --config selects a
        // hermetic single-file config, otherwise the layered install/user/project
        // resolution applies. A missing --config file is a fatal config error.
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

        // Load theme
        UIColors.LoadTheme();

        // Initialize shell environment (unless --nobash)
        try
        {
            _shellEnvironment = ShellEnvironment.Detect();
        }
        catch (InvalidOperationException ex)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]{Markup.Escape(ex.Message)}[/]");
            return;
        }
        if (!_noBash)
        {
            var bashTimeoutSeconds = int.TryParse(config["BashTimeoutSeconds"], out var bts) ? bts : 120;
            _bashTool = new BashTool(_shellEnvironment, defaultTimeoutSeconds: bashTimeoutSeconds);
            _readFileTool = new ReadFileTool(_shellEnvironment);
            _findFilesTool = new FindFilesTool(_shellEnvironment);
            _grepTool = new GrepTool(_shellEnvironment);
            _listDirTool = new ListDirTool(_shellEnvironment);
            _fetchUrlTool = new FetchUrlTool();

            var providerConfig = config.GetSection("ChatProviders").GetChildren()
                .FirstOrDefault(c => string.Equals(c["Name"], config["ActiveProvider"], StringComparison.OrdinalIgnoreCase));
            var useApplyPatch = string.Equals(providerConfig?["EditToolStyle"], "ApplyPatch", StringComparison.OrdinalIgnoreCase);
            if (useApplyPatch)
            {
                _applyPatchTool = new ApplyPatchTool(_shellEnvironment);
            }
            else
            {
                _writeFileTool = new WriteFileTool(_shellEnvironment);
                _editFileTool = new EditFileTool(_shellEnvironment);
            }
        }

        // Check for trust mode from config
        if (!_trustMode)
        {
            var trustSetting = config["Trust"];
            if (trustSetting?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                _trustMode = true;
        }

        // Initialize chat client using provider system
        var activeProviderName = config["ActiveProvider"] ?? string.Empty;
        _client = _providerManager.TryCreateChatClient(config)!;
        if (_client == null)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreError}]Failed to initialize chat client. Please check your configuration.[/]");
            _providerManager.ShowProviderStatus(config);
            Environment.Exit(1);
        }

        // Load the MCP manifest (--mcp overrides the default mcp.json).
        _mcpManager.LoadConfig(_mcpManifest);

        // Load fake tools
        await _fakeToolManager.LoadFakeToolsAsync();

        var isInteractiveMode = remainingArgs.Length == 0;

        // Initialize conversation manager with dependencies
        var maxToolCalls = int.TryParse(config["MaxToolCalls"], out var mtc) ? mtc : 25;
        var maxContextTokens = ResolveMaxContextTokens(config, activeProviderName);
        var compactionThreshold = double.TryParse(config["CompactionThreshold"], out var ct) ? ct : 0.75;
        var temperature = ResolveProviderFloat(config, activeProviderName, "Temperature");
        var presencePenalty = ResolveProviderFloat(config, activeProviderName, "PresencePenalty");
        var approvalPolicy = BuildApprovalPolicy(config);
        if (_bashTool != null) _bashTool.ApprovalPolicy = approvalPolicy;  // for the bash sandbox (Phase 5.3)
        _conversationManager = new ConversationManager(
            _client, _mcpManager, _fakeToolManager, _bashTool, _readFileTool, _writeFileTool, _editFileTool, _findFilesTool, _grepTool, _listDirTool, _fetchUrlTool, _applyPatchTool, approvalPolicy, activeProviderName, _verbose, _trustMode, maxToolCalls, maxContextTokens, compactionThreshold, _debugStream, temperature, presencePenalty);

        // Program mode: evaluate a conversation-program directly, with no default
        // persona injected — a bare program (or an explicit --spec) gets exactly
        // the directives written, nothing implicit. The default persona is the
        // floor only on the bare human/-p path (rules/preset-floor.md).
        if (_programFile != null || _specName != null || _validate || _resolve)
        {
            await RunProgramAsync(config, remainingArgs);
            _mcpManager.Dispose();
            return;
        }

        // Connect configured MCP servers and expose their tools to the model.
        // A manifest may auto-approve tools headlessly via alwaysAllow (["*"]).
        await _mcpManager.ConnectAllAsync();

        // The default preset: nb's persona (system.md + shell-env + provider/model
        // prompt layers + NB.md) expressed as first-class directives and evaluated
        // through the same engine as any conversation-program. This is the floor
        // for the bare human/-p/single-shot path only — a --program/--spec is
        // persona-free and never reaches here (rules/preset-floor.md).
        var presetWarnings = new List<string>();
        var preset = BuildDefaultPresetEvents(config, activeProviderName);
        var presetEvaluator = new ProgramEvaluator(_conversationManager, BuildProgramClient(config), presetWarnings);
        await presetEvaluator.EvaluateAsync(preset);
        foreach (var w in presetWarnings) Console.Error.WriteLine($"preset: {w}");

        // Show trust mode banner
        if (_trustMode)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]Trust mode active[/] [{UIColors.SpectreMuted}]— auto-approving within {Markup.Escape(_shellEnvironment.ShellCwd)}[/]");
        }

        // Initialize refactored services
        _commandProcessor = new CommandProcessor(_conversationManager, _configurationService, _providerManager);

        // Check if stdin is being piped
        string? stdinContent = null;
        if (Console.IsInputRedirected)
        {
            stdinContent = await Console.In.ReadToEndAsync();
        }

        // Execute based on mode
        if (remainingArgs.Length > 0 || stdinContent != null)
        {
            // Load a seed transcript as premise history (after the system prompt).
            LoadSeed();

            var userInput = BuildUserInput(remainingArgs, stdinContent);
            if (!string.IsNullOrWhiteSpace(userInput))
            {
                if (userInput.Trim() == "/tools")
                    HandleToolsCommand();
                else
                    await ExecuteSingleCommand(userInput);
            }
        }
        else
        {
            // Interactive mode: enable bracketed paste so multi-line pastes don't submit early
            bool bracketedPaste = !Console.IsInputRedirected;
            if (bracketedPaste) Console.Write("\x1b[?2004h");
            try
            {
                await StartChatLoop();
            }
            finally
            {
                if (bracketedPaste) Console.Write("\x1b[?2004l");
            }
        }
        
        // Cleanup MCP clients
        _mcpManager.Dispose();
    }



    private static async Task StartChatLoop()
    {
        // Get configured providers and current provider
        var config = _configurationService.GetConfiguration();
        var configuredProviders = _providerManager.GetConfiguredProviders(config);
        var currentProvider = _conversationManager.GetCurrentProvider();
        var providersList = string.Join(", ", configuredProviders.Select(p =>
            p == currentProvider ? $"[{UIColors.SpectreInfo}]{p}[/]" : $"[{UIColors.SpectreMuted}]{p}[/]"));

        // Get connected MCP servers
        var mcpServers = _mcpManager.GetConnectedServerNames();
        var mcpList = mcpServers.Count > 0
            ? string.Join(", ", mcpServers.Select(s => $"[{UIColors.SpectreMuted}]{s}[/]"))
            : "[dim]none[/]";

        Console.Write(" " + UIColors.robot_img_1);
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreMuted}]AI: [/]{providersList}");
        Console.Write(" " + UIColors.robot_img_2);
        AnsiConsole.MarkupLine($"  [{UIColors.SpectreMuted}]MCP: [/]{mcpList}");
        Console.Write(" " + UIColors.robot_img_3);
        AnsiConsole.MarkupLine($"  NotaBene 0.10.0β [{UIColors.SpectreMuted}]▪[/] [{UIColors.SpectreAccent}]/[/] [{UIColors.SpectreMuted}]for commands[/]");
        
        while (true)
        {
            // Add visual separator before user input
            string divider = string.Concat(Enumerable.Repeat("🞌", Console.WindowWidth));
            Console.WriteLine($"{UIColors.NativeMuted}{divider}{UIColors.NativeReset}");

            var userInput = _lineEditor.ReadLine($"\u001b[38;5;154m›\u001b[0m {UIColors.NativeUserInput}");
            Console.Write(UIColors.NativeReset);
            
            // Add visual separator after user input
            Console.WriteLine($"{UIColors.NativeMuted}{divider}{UIColors.NativeReset}");

            if (string.IsNullOrWhiteSpace(userInput))
                continue;

            // Tool listing command
            if (userInput.Trim() == "/tools")
            {
                HandleToolsCommand();
                continue;
            }

            // Process command through the command processor
            var result = _commandProcessor.ProcessCommand(userInput);

            switch (result.Action)
            {
                case CommandAction.Exit:
                    return;
                
                case CommandAction.Continue:
                    // Check if this was a non-command that should go to LLM
                    if (!userInput.TrimStart().StartsWith("/"))
                    {
                        await _conversationManager.SendMessageAsync(userInput);
                    }
                    break;

                case CommandAction.SendToLlm:
                    await _conversationManager.SendMessageAsync(result.ModifiedInput!);
                    break;

            }
        }
    }

    private static void HandleToolsCommand()
    {
        var tools = _conversationManager.GetAvailableTools();
        if (tools.Count == 0)
        {
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]No tools available.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Tool");
        table.AddColumn("Approval");

        foreach (var group in tools.GroupBy(t => t.Group))
        {
            table.AddEmptyRow();
            table.AddRow($"[{UIColors.SpectreInfo}]{Markup.Escape(group.Key)}[/]", "");
            foreach (var t in group)
            {
                var color = t.Approval.StartsWith("auto") ? UIColors.SpectreSuccess : UIColors.SpectreWarning;
                table.AddRow($"  {Markup.Escape(t.Name)}", $"[{color}]{Markup.Escape(t.Approval)}[/]");
            }
        }
        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]auto = no prompt · (cwd) within the working-dir sandbox · (trust) needs --trust · (always-allow) listed in mcp.json[/]");
    }

    private static async Task ExecuteSingleCommand(string userInput)
    {
        // Process command through the command processor (same logic as interactive mode)
        var result = _commandProcessor.ProcessCommand(userInput);

        switch (result.Action)
        {
            case CommandAction.Exit:
                // Exit command processed, just return
                return;

            case CommandAction.Continue:
                // Check if this was a non-command that should go to LLM
                if (!userInput.TrimStart().StartsWith("/"))
                {
                    await _conversationManager.SendMessageAsync(userInput);
                }
                break;

        }

        if (_outputMode == "jsonl")
            EmitJsonlTranscript();
        else if (_outputMode == "porcelain")
            EmitPorcelainTranscript();

        // The exit-code contract: a single-shot run reports why it ended in $?
        // (0 ok · 2 provider error · 3 aborted · 4 approval denied). See
        // Transcript/ExitReasons.cs.
        Environment.ExitCode = ExitReasons.ToExitCode(_conversationManager.LastOutcome);
    }

    // Load a --seed transcript and append it as premise history. Fails fast with a
    // model-fixable error on a missing file or an invalid transcript.
    private static void LoadSeed()
    {
        if (_seedFile == null) return;
        if (!File.Exists(_seedFile))
        {
            Console.Error.WriteLine($"Error: seed file not found: {_seedFile}");
            Environment.Exit(1);
        }

        try
        {
            var warnings = new List<string>();
            var messages = TranscriptLoader.Load(File.ReadAllText(_seedFile), warnings);
            foreach (var w in warnings)
                AnsiConsole.MarkupLine($"[{UIColors.SpectreWarning}]seed: {Markup.Escape(w)}[/]");

            // A seed's own system messages now survive. Under the preset-floor
            // model `system` is a plain message, not a singular owned prompt
            // (rules/preset-floor.md clause 3): the persona arrives as the default
            // preset's SystemEvent and the seed's system messages append after it
            // as additional premise, no different from any other seeded turn.
            _conversationManager.AppendHistory(messages);
            AnsiConsole.MarkupLine($"[{UIColors.SpectreMuted}]Seeded {messages.Count} message(s) from {Markup.Escape(_seedFile)}[/]");
        }
        catch (TranscriptFormatException ex)
        {
            Console.Error.WriteLine($"Error: invalid seed transcript: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // Build the completed conversation as transcript events plus a run-level
    // result trailer with token usage — the shared input to both machine emits.
    private static (List<TranscriptEvent> events, ResultEvent trailer) BuildTranscript()
    {
        var events = TranscriptMapper.FromHistory(_conversationManager.History);
        UsageInfo? usage = _conversationManager.TotalUsage is { } u
            ? new UsageInfo { Input = u.input, Output = u.output, Total = u.total }
            : null;
        return (events, TranscriptMapper.ResultTrailer(events, _conversationManager.LastOutcome, usage));
    }

    // Emit the transcript schema as JSONL on stdout (trailer inline). Chrome has
    // already been diverted to stderr (see the --output seam in Main).
    private static void EmitJsonlTranscript()
    {
        var (events, trailer) = BuildTranscript();
        var all = new List<TranscriptEvent>(events) { trailer };
        Console.Out.Write(TranscriptSerializer.Serialize(all));
        Console.Out.Flush();
    }

    // Emit the same events as porcelain text on stdout; the run trailer goes to
    // stderr so stdout stays parseable (TOOL/RESULT lines + verbatim prose).
    private static void EmitPorcelainTranscript()
    {
        var (events, trailer) = BuildTranscript();
        Console.Out.Write(TranscriptPorcelainWriter.Write(events));
        Console.Out.Flush();
        Console.Error.WriteLine(TranscriptPorcelainWriter.Trailer(trailer));
    }

    // Assemble nb's default persona as a conversation-program preset: the
    // directives the bare human/-p/single-shot path opts into. Today the persona
    // is a single system message (base prompt + shell-env section + provider- and
    // provider+model-specific prompt layers + NB.md project context), so the
    // preset is one SystemEvent; expressing it as an event list keeps it a
    // first-class, evaluator-routed directive rather than an engine-injected
    // prompt (rules/preset-floor.md clause 2).
    private static List<TranscriptEvent> BuildDefaultPresetEvents(IConfiguration config, string activeProviderName)
    {
        var basePrompt = LoadSystemPrompt();
        var fullPrompt = _noBash
            ? basePrompt
            : $"{basePrompt}\n\n{_shellEnvironment.BuildSystemPromptSection()}";

        // Append provider-specific system prompt if present (e.g. system.AzureFoundry.md)
        var providerPromptPath = Path.Combine(AppContext.BaseDirectory, "prompts", $"system.{activeProviderName}.md");
        if (File.Exists(providerPromptPath))
            fullPrompt += $"\n\n{File.ReadAllText(providerPromptPath)}";

        // Append provider+model-specific prompt if present (e.g. system.LocalLlm.qwen-coder.md).
        // Lets one provider host multiple models with distinct quirks.
        var activeModelSlug = ResolveActiveModelSlug(config, activeProviderName);
        if (!string.IsNullOrEmpty(activeModelSlug))
        {
            var modelPromptPath = Path.Combine(AppContext.BaseDirectory, "prompts", $"system.{activeProviderName}.{activeModelSlug}.md");
            if (File.Exists(modelPromptPath))
                fullPrompt += $"\n\n{File.ReadAllText(modelPromptPath)}";
        }

        // Auto-load project context from NB.md in working directory
        var projectContext = LoadProjectContext(_noBash ? "." : _shellEnvironment.ShellCwd);
        if (projectContext != null)
            fullPrompt += $"\n\n{projectContext}";

        return new List<TranscriptEvent> { new SystemEvent { Text = fullPrompt } };
    }

    // Evaluate a conversation-program: an optional --spec prefix (a reusable run
    // of config directives) followed by the body — a --program file, or the
    // prompt args as a single `run`. Detects source vs bytecode, walks the
    // directives (swapping the client on provider/model changes), emits.
    private static async Task RunProgramAsync(IConfiguration config, string[] promptArgs)
    {
        var warnings = new List<string>();
        List<TranscriptEvent> program;
        try
        {
            program = await BuildProgramAsync(config, promptArgs, warnings);
        }
        catch (Exception ex) when (ex is TranscriptFormatException or ProgramParseException or FileNotFoundException or SpecNotFoundException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        // Inspection modes parse + resolve but run nothing (no MCP connect needed).
        if (_validate) { ValidateProgram(program, config, warnings); return; }
        if (_resolve) { ResolveProgram(program); return; }

        // Connect configured MCP servers so an `mcp +server` directive has servers
        // to select. The tool surface still starts strict-empty (ToolSurface.Fold):
        // a program with no `mcp` directive exposes none of them.
        await _mcpManager.ConnectAllAsync();

        var evaluator = new ProgramEvaluator(_conversationManager, BuildProgramClient(config), warnings);
        try
        {
            await evaluator.EvaluateAsync(program);
        }
        catch (TranscriptFormatException ex)
        {
            // A fabricated tool round the program authored is malformed (unpaired
            // call/result, non-monotonic turns). Fail fast like a bad seed.
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }
        catch (SandboxUnavailableException ex)
        {
            // An `approval sandbox bwrap` directive on a host that can't honor it.
            Console.Error.WriteLine($"Error: {ex.Message}");
            Environment.ExitCode = 1;
            return;
        }

        foreach (var w in warnings) Console.Error.WriteLine($"program: {w}");

        // An `output` directive in the program wins over the flag/default.
        var mode = evaluator.OutputMode ?? _outputMode;
        if (mode == "porcelain") EmitPorcelainTranscript(); else EmitJsonlTranscript();

        Environment.ExitCode = ExitReasons.ToExitCode(_conversationManager.LastOutcome);
    }

    // Assemble the full program: an optional --spec prefix followed by the body
    // (a --program file, or the prompt args as a single run).
    private static async Task<List<TranscriptEvent>> BuildProgramAsync(IConfiguration config, string[] promptArgs, IList<string> warnings)
    {
        var program = new List<TranscriptEvent>();

        if (_specName != null)
            program.AddRange(ResolveSpecEvents(_specName, config, warnings));

        if (_programFile != null)
        {
            var source = _programFile == "-"
                ? await Console.In.ReadToEndAsync()
                : await File.ReadAllTextAsync(_programFile);
            program.AddRange(ParseProgramSource(source, warnings));
        }
        else if (promptArgs.Length > 0)
        {
            program.Add(new RunEvent { Prompt = string.Join(" ", promptArgs) });
        }

        return program;
    }

    // --validate: parse succeeded (we got here); report structural warnings and
    // semantic errors (unknown provider, bad output mode), run nothing. Exit 1 on
    // any error, 0 otherwise.
    private static void ValidateProgram(IReadOnlyList<TranscriptEvent> program, IConfiguration config, IList<string> warnings)
    {
        var providers = _providerManager.GetConfiguredProviders(config)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (var ev in program)
        {
            switch (ev)
            {
                case ProviderEvent p when providers.Count > 0 && !providers.Contains(p.Name):
                    errors.Add($"unknown provider '{p.Name}'. Configured: {string.Join(", ", providers)}.");
                    break;
                case OutputEvent o when o.Mode is not ("interactive" or "porcelain" or "jsonl"):
                    errors.Add($"invalid output mode '{o.Mode}'. Valid: interactive, porcelain, jsonl.");
                    break;
                case ApprovalEvent a when a.Key is not ("bash" or "mcp" or "default" or "sandbox"):
                    errors.Add($"invalid approval key '{a.Key}'. Valid: bash, mcp, default, sandbox.");
                    break;
                case ApprovalEvent { Key: "default" } a when a.Value is not ("prompt" or "deny"):
                    errors.Add($"invalid approval default '{a.Value}'. Valid: prompt, deny.");
                    break;
                case ApprovalEvent { Key: "sandbox" } a when !BwrapSandbox.TryParse(a.Value, out _, out _):
                    errors.Add($"invalid approval sandbox '{a.Value}'. Valid: none, bwrap, bwrap-net.");
                    break;
            }
        }

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

    // --resolve: walk the directives without invoking, printing the effective
    // envelope at each run point (the ordering inspector for anywhere-config).
    private static void ResolveProgram(IReadOnlyList<TranscriptEvent> program)
    {
        string provider = "(default)", model = "(default)", output = _outputMode;
        var surfaceDirectives = new List<SurfaceDirectiveEvent>();
        string approvalDefault = "prompt", sandbox = "none";
        int bashRules = 0, mcpRules = 0;
        int run = 0;

        foreach (var ev in program)
        {
            switch (ev)
            {
                case ProviderEvent p: provider = p.Name; break;
                case ModelEvent m: model = m.Name; break;
                case OutputEvent o: output = o.Mode; break;
                case SurfaceDirectiveEvent sd: surfaceDirectives.Add(sd); break;
                case ApprovalEvent { Key: "default" } a: approvalDefault = a.Value; break;
                case ApprovalEvent { Key: "sandbox" } a: sandbox = a.Value; break;
                case ApprovalEvent { Key: "bash" }: bashRules++; break;
                case ApprovalEvent { Key: "mcp" }: mcpRules++; break;
                case RunEvent:
                    run++;
                    // Fold through the same resolver the evaluator runs, so what this
                    // prints is provably what a run exposes (plans/tool-surface-directives.md).
                    var surface = ToolSurface.Fold(surfaceDirectives, ConversationManager.NativeToolNames);
                    var mcpStr = surface.McpServers is { Count: > 0 } s ? string.Join(",", s) : "(none)";
                    var toolStr = surface.NativeAllow is null
                        ? "all"
                        : surface.NativeAllow.Count > 0 ? string.Join(",", surface.NativeAllow.OrderBy(n => n)) : "(none)";
                    Console.WriteLine($"run {run}: provider={provider} model={model} output={output} mcp=[{mcpStr}] tools={toolStr} approval={approvalDefault}(bash:{bashRules} mcp:{mcpRules}) sandbox={sandbox}");
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

    // Text-source built-in specs ship in the box. headless = machine output, no
    // persona. (The `chat` built-in is computed, not text — see ResolveSpecEvents.)
    private static readonly Dictionary<string, string> BuiltInSpecs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["headless"] = "output jsonl\n",
    };

    // Resolve a --spec argument to its events. `chat` is the one computed
    // built-in: it hands back the default persona preset (the same directives the
    // bare human/-p path opts into — rules/preset-floor.md clause 2), so a program
    // can request the persona floor explicitly (`nb --spec chat --program p.nb`).
    // Every other spec is text, resolved to source and parsed.
    private static IEnumerable<TranscriptEvent> ResolveSpecEvents(string spec, IConfiguration config, IList<string> warnings) =>
        string.Equals(spec, "chat", StringComparison.OrdinalIgnoreCase)
            ? BuildDefaultPresetEvents(config, config["ActiveProvider"] ?? string.Empty)
            : ParseProgramSource(ResolveSpecSource(spec), warnings);

    // Resolve a text-spec argument to its source: an explicit file path, then a
    // built-in name, then specs/<name>.nb in the project (.nb, upward walk) or
    // user (~/.config/nb) config layers.
    private static string ResolveSpecSource(string spec)
    {
        if (File.Exists(spec)) return File.ReadAllText(spec);
        if (BuiltInSpecs.TryGetValue(spec, out var builtin)) return builtin;
        foreach (var dir in SpecSearchDirs())
        {
            var path = Path.Combine(dir, spec + ".nb");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new SpecNotFoundException(
            $"unknown spec '{spec}'. Built-in: chat, {string.Join(", ", BuiltInSpecs.Keys)}. " +
            $"Or give a path, or define specs/{spec}.nb in a .nb dir or ~/.config/nb.");
    }

    private static IEnumerable<string> SpecSearchDirs()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            yield return Path.Combine(dir, ".nb", "specs");
            dir = Path.GetDirectoryName(dir);
        }
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var baseDir = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        yield return Path.Combine(baseDir, "nb", "specs");
    }

    // (provider, model) -> a chat client for the program evaluator. Either may be
    // null (fall back to the configured ActiveProvider). A model override is
    // written into the provider's config block before the client is built.
    private static Func<string?, string?, IChatClient?> BuildProgramClient(IConfiguration config) =>
        (provider, model) =>
        {
            var name = provider ?? config["ActiveProvider"];
            if (name != null && model != null) OverrideProviderModel(config, name, model);
            return _providerManager.TryCreateChatClient(config, name);
        };

    private static void OverrideProviderModel(IConfiguration config, string providerName, string model)
    {
        var children = config.GetSection("ChatProviders").GetChildren().ToList();
        for (int i = 0; i < children.Count; i++)
            if (string.Equals(children[i]["Name"], providerName, StringComparison.OrdinalIgnoreCase))
            {
                // Write both keys: most providers read "Model", but classic
                // AzureOpenAI reads "ChatDeploymentName". Setting both lets a
                // program's `model` directive land whichever field the provider
                // reads, without the override needing to know the provider kind.
                config[$"ChatProviders:{i}:Model"] = model;
                config[$"ChatProviders:{i}:ChatDeploymentName"] = model;
                return;
            }
    }

    // Resolve an @file include for the source parser: relative to the program
    // file's directory (or cwd for a stdin program), fail fast if missing.
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

/// <summary>Raised when a --spec name resolves to no built-in, path, or config-layer spec file.</summary>
public sealed class SpecNotFoundException(string message) : Exception(message);