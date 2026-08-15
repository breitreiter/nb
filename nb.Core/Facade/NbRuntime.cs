using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using nb.Harness;
using nb.MCP;
using nb.Providers;
using nb.Shell;

namespace nb;

/// <summary>
/// Assembles the engine pipeline for one run — the same wiring the CLI does in
/// <c>Program.Main</c>, but with fresh managers and, crucially, **throwing
/// <see cref="NbStartupException"/> instead of calling <c>Environment.Exit</c>** so a
/// library host is never killed. Owns the <see cref="ProviderManager"/>/<see cref="McpManager"/>/
/// <see cref="FakeToolManager"/> it builds and disposes the MCP clients. Internal: no
/// engine type crosses the facade boundary (see plans/composable-cli-reorientation.md,
/// Pillar 5). CLI parity with Program's inline wiring is deliberate; 6b unifies them.
/// </summary>
internal sealed class NbRuntime : IDisposable
{
    private readonly IConfiguration _config;
    private readonly ProviderManager _providers;

    public ConversationManager Conversation { get; }
    public McpManager Mcp { get; }

    /// <summary>
    /// Non-fatal problems found while assembling the run — deprecated configuration, so
    /// far. Distinct from the evaluator's warnings, which are about the program; these are
    /// about the environment the program was handed. Both end up on the same
    /// <c>RunResult.Warnings</c> list, because a caller does not care which layer noticed.
    /// </summary>
    public IReadOnlyList<string> StartupWarnings { get; }

    private NbRuntime(IConfiguration config, ProviderManager providers, McpManager mcp,
        ConversationManager conversation, IReadOnlyList<string> startupWarnings)
    {
        _config = config;
        _providers = providers;
        Mcp = mcp;
        Conversation = conversation;
        StartupWarnings = startupWarnings;
    }

    // The client factory the evaluator uses for mid-stream provider/model swaps —
    // bound to this runtime's own ProviderManager (mirrors Program.BuildProgramClient).
    public Func<string?, string?, IChatClient?> ClientFactory => (provider, model) =>
    {
        var name = provider ?? _config["ActiveProvider"];
        if (name != null && model != null) ProviderConfigResolver.OverrideProviderModel(_config, name, model);
        return _providers.TryCreateChatClient(_config, name);
    };

    public static async Task<NbRuntime> BuildAsync(IConfiguration config, NbOptions options)
    {
        var startupWarnings = new List<string>();

        ShellEnvironment shell;
        try { shell = ShellEnvironment.Detect(); }
        catch (InvalidOperationException ex) { throw new NbStartupException(ex.Message); }

        BashTool? bash = null;
        ReadFileTool? readFile = null;
        WriteFileTool? writeFile = null;
        EditFileTool? editFile = null;
        FindFilesTool? findFiles = null;
        GrepTool? grep = null;
        ListDirTool? listDir = null;
        FetchUrlTool? fetchUrl = null;
        SearchWebTool? searchWeb = null;
        ApplyPatchTool? applyPatch = null;
        bool applyPatchStyle = false;
        if (!options.NoBash)
        {
            var bashTimeout = int.TryParse(config["BashTimeoutSeconds"], out var bts) ? bts : 120;
            bash = new BashTool(shell, defaultTimeoutSeconds: bashTimeout);
            readFile = new ReadFileTool(shell);
            findFiles = new FindFilesTool(shell);
            grep = new GrepTool(shell);
            listDir = new ListDirTool(shell);
            fetchUrl = new FetchUrlTool();
            try { searchWeb = SearchWebTool.FromConfig(config["Search:Provider"], config["Search:ApiKey"]); }
            catch (ArgumentException ex) { throw new NbStartupException(ex.Message); }

            // Every edit tool is built; EditToolStyle decides only which of them nb's own
            // surface *advertises*. The runtime used to build one and not the other, which
            // meant a costume inherited a hole from provider config it had no say in —
            // the qwen costume lost its edit tools under EditToolStyle: ApplyPatch, and the
            // codex costume, which needs apply_patch, would have had none without it.
            // Construction is cheap and advertisement is the harness's business.
            // (plans/harness-emulation.md, "No clutter, either")
            writeFile = new WriteFileTool(shell);
            editFile = new EditFileTool(shell);
            applyPatch = new ApplyPatchTool(shell);

            var providerConfig = config.GetSection("ChatProviders").GetChildren()
                .FirstOrDefault(c => string.Equals(c["Name"], config["ActiveProvider"], StringComparison.OrdinalIgnoreCase));

            // DEPRECATED (2026-08-15). EditToolStyle is a proto-harness: it predates the
            // idea, and says a fraction of what `harness codex` says properly. It still
            // works and still means what it meant — the deprecation is a warning and a
            // documented replacement, not a behaviour change — because it is documented
            // config that is presumably in use. See plans/harness-emulation.md, step 7.
            var editToolStyle = providerConfig?["EditToolStyle"];
            applyPatchStyle = string.Equals(editToolStyle, "ApplyPatch", StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(editToolStyle))
                startupWarnings.Add(EditToolStyleDeprecation(providerConfig!["Name"], editToolStyle, applyPatchStyle));
        }

        var trust = options.Trust || string.Equals(config["Trust"], "true", StringComparison.OrdinalIgnoreCase);

        var providers = new ProviderManager(options.ProvidersDirectory ?? config["ProvidersPath"]);
        var providerName = config["ActiveProvider"] ?? string.Empty;
        var client = providers.TryCreateChatClient(config);
        if (client == null)
            throw new NbStartupException($"Failed to initialize chat client for provider '{providerName}'. Check the configuration.");

        var mcp = new McpManager();
        // An explicit --mcp manifest that can't be read is a startup failure, not an
        // empty tool surface: the run was asked to be hermetic against that file.
        try { mcp.LoadConfig(options.McpManifestPath); }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
        { throw new NbStartupException(ex.Message); }

        var fakeTools = new FakeToolManager();
        await fakeTools.LoadFakeToolsAsync();

        var approval = BuildApprovalPolicy(config, options, trust, mcp);
        if (bash != null) bash.ApprovalPolicy = approval;

        var maxToolCalls = int.TryParse(config["MaxToolCalls"], out var mtc) ? mtc : 25;
        var maxContextTokens = ProviderConfigResolver.ResolveMaxContextTokens(config, providerName);
        var compactionThreshold = double.TryParse(config["CompactionThreshold"], out var ct) ? ct : 0.75;
        var temperature = ProviderConfigResolver.ResolveProviderFloat(config, providerName, "Temperature");
        var presencePenalty = ProviderConfigResolver.ResolveProviderFloat(config, providerName, "PresencePenalty");

        // Doom loop: option overrides config; absent => default (enabled, threshold 3);
        // 0 or less => disabled. Token budget: option overrides config; 0/absent => unlimited.
        int? doomOpt = options.DoomLoopThreshold ?? (int.TryParse(config["DoomLoopThreshold"], out var dt) ? dt : null);
        bool doomEnabled = doomOpt is null || doomOpt > 0;
        int doomThreshold = doomOpt is int dv && dv >= 2 ? dv : DoomLoopDetector.DefaultThreshold;
        long? tokenBudget = options.TokenBudget ?? (long.TryParse(config["TokenBudget"], out var tb) ? tb : null);
        if (tokenBudget is <= 0) tokenBudget = null;
        long? wallBudgetMs = options.WallClockBudgetMs ?? (long.TryParse(config["WallClockBudgetMs"], out var wb) ? wb : null);
        if (wallBudgetMs is <= 0) wallBudgetMs = null;

        var harness = new NbHarness(bash, readFile, writeFile, editFile, findFiles, grep, listDir, fetchUrl, searchWeb, applyPatch, applyPatchStyle);

        var conversation = new ConversationManager(
            client, mcp, fakeTools, harness,
            approval, providerName, options.Verbose, trust, maxToolCalls, maxContextTokens, compactionThreshold,
            debugStream: false, temperature, presencePenalty,
            doomLoopThreshold: doomThreshold, doomLoopEnabled: doomEnabled, tokenBudget: tokenBudget,
            wallClockBudgetMs: wallBudgetMs);

        return new NbRuntime(config, providers, mcp, conversation, startupWarnings);
    }

    /// <summary>
    /// The deprecation notice, naming the entry so it is findable in a layered config, and
    /// pointing at the directive that replaces it. `harness codex` is the replacement for
    /// <c>ApplyPatch</c> because apply_patch *is* the Codex edit surface, and the costume
    /// brings the rest of that surface with it rather than a fragment of it.
    /// </summary>
    private static string EditToolStyleDeprecation(string? entryName, string value, bool applyPatch)
    {
        var where = string.IsNullOrEmpty(entryName) ? "the active provider entry" : $"provider entry '{entryName}'";
        var replacement = applyPatch
            ? "use the program directive `harness codex` instead, which advertises apply_patch as part of the whole Codex surface"
            : "it is already the default and can simply be removed";

        return $"EditToolStyle is deprecated (set to '{value}' on {where}) — {replacement}. "
             + "It still works and still means what it meant; it will be removed in a future version.";
    }

    // Build the approval policy from the Approval config block + the run's approve
    // patterns. Mirrors Program.BuildApprovalPolicy but THROWS on an unhonorable
    // sandbox request instead of exiting the process.
    private static ApprovalPolicy BuildApprovalPolicy(IConfiguration config, NbOptions options, bool trust, McpManager mcp)
    {
        var bashPatterns = new ApprovalPatterns(options.ApprovePatterns);
        foreach (var entry in config.GetSection("Approval:Bash").GetChildren())
            if (!string.IsNullOrEmpty(entry.Value)) bashPatterns.Add(entry.Value);

        var mcpGlobs = config.GetSection("Approval:McpTools").GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!);

        var def = string.Equals(config["Approval:Default"], "deny", StringComparison.OrdinalIgnoreCase)
            ? ApprovalDefault.Deny : ApprovalDefault.Prompt;

        var policy = new ApprovalPolicy(trust, bashPatterns, mcp.IsAlwaysAllowed, mcpGlobs, def);

        // search_web has no argument worth pattern-matching, so it grants as a flag:
        // Approval.Search in config, or the `approval search allow` directive.
        if (string.Equals(config["Approval:Search"], "true", StringComparison.OrdinalIgnoreCase))
            policy.SetSearchAllowed(true);

        var sandboxValue = config["Approval:Sandbox"];
        if (!string.IsNullOrEmpty(sandboxValue))
        {
            if (!BwrapSandbox.TryParse(sandboxValue, out var mode, out var allowNet))
                throw new NbStartupException($"invalid Approval.Sandbox '{sandboxValue}'. Valid: none, bwrap, bwrap-net.");
            if (mode == SandboxMode.Bwrap && !BwrapSandbox.IsAvailable())
                throw new NbStartupException("Sandbox 'bwrap' requested but bubblewrap (bwrap) is not available on this host (Linux + bwrap on PATH required).");
            policy.SetSandbox(mode, allowNet);
        }

        return policy;
    }

    public void Dispose() => Mcp.Dispose();
}
