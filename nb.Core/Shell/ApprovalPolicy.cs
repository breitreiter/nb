using System.Text.RegularExpressions;

namespace nb.Shell;

/// <summary>Whether a tool call auto-approves or is refused. There is no third outcome:
/// nb never prompts, so a call the ladder does not allow is denied
/// (plans/approval-without-prompts.md).</summary>
public enum ApprovalDecision { Allow, Deny }

/// <summary>
/// How permissive a run is with calls nothing explicitly allow-listed.
///
/// Both tiers now *deny* an unmatched call — the difference is how much ladder a call gets
/// to climb before it counts as unmatched. <c>Prompt</c> runs the whole auto-approve ladder:
/// explicit patterns, then the built-in safe-command list, then <c>Trust</c> + sandbox.
/// <c>Deny</c> honours the explicit allow-list and nothing else, because the safe list and
/// trust are both implicit grants and the safe list includes <c>make</c>, <c>npx</c> and
/// <c>go build</c> (arbitrary code, not just reads). So these are permissiveness tiers, not
/// dispositions; see <see cref="ApprovalPolicy.DecideBash"/>.
///
/// <c>Prompt</c> keeps its name because it is the wire spelling — <c>approval default
/// prompt</c> in a program, <c>"Default": "prompt"</c> in config — and renaming it would
/// break published grammar for no behavioural gain. Read it as "the permissive tier".
/// </summary>
public enum ApprovalDefault { Prompt, Deny }

/// <summary>How the bash child is contained: not at all, or under a bwrap sandbox.</summary>
public enum SandboxMode { None, Bwrap }

/// <summary>
/// The resolved approval policy: which tool calls auto-approve and what an
/// unmatched call does (<see cref="Default"/>). Seeded from <c>--trust</c> /
/// <c>--approve</c> / <c>alwaysAllow</c> and an <c>Approval</c> config block, and
/// layered further by the <c>approval</c> conversation-program directive (its
/// mutators). Carries the bash <see cref="Sandbox"/> mode (Phase 5.3). See
/// plans/approval-policy-and-sandbox.md. The policy only chooses the decision;
/// rendering the refusal — to the model and to the human — stays at the call site
/// (<see cref="nb.Harness.NbHarness.Deny"/>).
/// </summary>
public sealed class ApprovalPolicy
{
    private readonly bool _trust;
    private readonly ApprovalPatterns _bashPatterns;       // --approve + Approval.Bash + `approval bash`
    private readonly Func<string, bool> _mcpAlwaysAllowed;  // McpManager.IsAlwaysAllowed (mcp.json)
    private readonly List<Regex> _mcpGlobs = new();         // Approval.McpTools + `approval mcp`
    private ApprovalDefault _default;
    private SandboxMode _sandbox;                            // Approval.Sandbox + `approval sandbox`
    private bool _sandboxNet;                                // bwrap-net opts network back in
    private bool _searchAllowed;                             // Approval.Search + `approval search allow`
    private bool _fetchAllowed;                              // Approval.Fetch  + `approval fetch allow`

    public ApprovalPolicy(bool trust, ApprovalPatterns bashPatterns, Func<string, bool> mcpAlwaysAllowed,
        IEnumerable<string>? mcpGlobs = null, ApprovalDefault @default = ApprovalDefault.Prompt)
    {
        _trust = trust;
        _bashPatterns = bashPatterns;
        _mcpAlwaysAllowed = mcpAlwaysAllowed;
        _default = @default;
        if (mcpGlobs != null)
            foreach (var g in mcpGlobs) AddMcpGlob(g);
    }

    public bool TrustMode => _trust;
    public ApprovalDefault Default => _default;
    public SandboxMode Sandbox => _sandbox;
    public bool SandboxNet => _sandboxNet;

    // The `approval` directive layers onto the config-seeded state (Phase 5.2b).
    public void SetDefault(ApprovalDefault d) => _default = d;
    public void AddBashPattern(string pattern) => _bashPatterns.Add(pattern);

    /// <summary>Set the bash sandbox mode (Phase 5.3). Availability is the caller's
    /// responsibility to probe (<see cref="BwrapSandbox.IsAvailable"/>) before a run.</summary>
    public void SetSandbox(SandboxMode mode, bool allowNet = false)
    {
        _sandbox = mode;
        _sandboxNet = allowNet;
    }

    /// <summary>
    /// Add an MCP allow glob. Matched against the exposed <c>{server}_{tool}</c>
    /// composite name; <c>/</c> is accepted as an alias for the <c>_</c> separator
    /// (so <c>weather/*</c> matches <c>weather_current</c>).
    /// </summary>
    public void AddMcpGlob(string glob)
    {
        var pattern = "^" + Regex.Escape(glob.Replace('/', '_')).Replace("\\*", ".*") + "$";
        _mcpGlobs.Add(new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase));
    }

    // The disposition for a call nothing auto-approved. Denial either way — the tier
    // already had its say in how far the call got to climb. Kept as a named member so the
    // decide methods read as "…else the unmatched disposition" rather than a bare Deny,
    // and so callers consult Default (not the decision) to say *which* denial it was.
    private const ApprovalDecision NonMatch = ApprovalDecision.Deny;

    /// <summary>
    /// Bash precedence: <c>--approve</c>/<c>Approval.Bash</c> match → safe-command
    /// allowlist (non-dangerous) → trust+sandbox → else <see cref="Default"/>.
    /// <c>Reason</c> labels an <see cref="ApprovalDecision.Allow"/> so the caller
    /// preserves its log line (pre-approved / safe / trust).
    ///
    /// Under <see cref="ApprovalDefault.Deny"/> only the explicit allow-list applies:
    /// the built-in safe-command list and <c>--trust</c> are both implicit grants, and
    /// a program that asks for <c>approval default deny</c> means denial — otherwise
    /// the safe list (which includes <c>make</c>, <c>npx</c>, <c>go build</c>: arbitrary
    /// code, not just reads) silently outranks it.
    /// </summary>
    public (ApprovalDecision Decision, string? Reason) DecideBash(string command, ClassifiedCommand classified, string cwd, bool bashPresent)
    {
        if (_bashPatterns.IsApproved(command))
            return (ApprovalDecision.Allow, "pre-approved");

        if (_default == ApprovalDefault.Deny)
            return (ApprovalDecision.Deny, null);

        if (!classified.IsDangerous && IsSafeCommand(command))
            return (ApprovalDecision.Allow, "safe");

        if (_trust && !classified.IsDangerous && bashPresent && IsBashCommandTrusted(classified, cwd))
            return (ApprovalDecision.Allow, "trust");

        return (NonMatch, null);
    }

    /// <summary>MCP: an <c>alwaysAllow</c>- or <c>Approval.McpTools</c>-matched tool auto-approves, else <see cref="Default"/>.</summary>
    public ApprovalDecision DecideMcp(string compositeToolName) =>
        _mcpAlwaysAllowed(compositeToolName) || _mcpGlobs.Any(r => r.IsMatch(compositeToolName))
            ? ApprovalDecision.Allow : NonMatch;

    /// <summary>File tools: an in-sandbox path auto-approves, else <see cref="Default"/>.</summary>
    public ApprovalDecision DecidePath(bool inSandbox) =>
        inSandbox ? ApprovalDecision.Allow : NonMatch;

    /// <summary>
    /// fetch_url auto-approves only when explicitly allow-listed (<c>Approval.Fetch</c> or
    /// the <c>approval fetch allow</c> directive), else falls to <see cref="Default"/>.
    /// Like search it is a single capability with no argument worth pattern-matching. It
    /// needs its own key rather than riding on <c>search</c>: reaching an arbitrary URL and
    /// running a web search are different grants, and a program allowing one should not
    /// silently acquire the other.
    /// </summary>
    public ApprovalDecision DecideFetch() =>
        _fetchAllowed ? ApprovalDecision.Allow : NonMatch;

    /// <summary>
    /// search_web auto-approves only when explicitly allow-listed (<c>Approval.Search</c>
    /// or <c>--approve search_web</c>), else falls to <see cref="Default"/>. The opt-in
    /// exists because nb's primary diagnostic mode is non-interactive, where an
    /// unapprovable tool can never execute — search intent would still be recorded, but
    /// every headless run would read as a denial regardless of configuration.
    /// </summary>
    public ApprovalDecision DecideSearch() =>
        _searchAllowed ? ApprovalDecision.Allow : NonMatch;

    public void SetSearchAllowed(bool allowed) => _searchAllowed = allowed;

    public void SetFetchAllowed(bool allowed) => _fetchAllowed = allowed;

    // Commands that are always safe to run without approval.
    // Matched against the first token of the command (before pipes/args).
    private static readonly HashSet<string> SafeCommandPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Build
        "dotnet build", "dotnet test", "dotnet run", "dotnet restore",
        "npm run", "npm test", "npx", "yarn build", "yarn test",
        "cargo build", "cargo test", "cargo check", "cargo clippy",
        "go build", "go test", "go vet",
        "make", "cmake",
        "mvn compile", "mvn test", "mvn package",
        "gradle build", "gradle test",
        "python -m pytest", "pytest",

        // Git (read-only)
        "git status", "git diff", "git log", "git show", "git branch",
        "git stash list", "git remote", "git tag",

        // Read-only tools
        "ls", "pwd", "which", "whereis", "file", "wc", "du", "df",
        "env", "echo", "date", "uname", "whoami",
    };

    private static bool IsSafeCommand(string command)
    {
        var trimmed = command.Trim();
        // Check if command starts with any safe prefix
        foreach (var prefix in SafeCommandPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && (trimmed.Length == prefix.Length || trimmed[prefix.Length] is ' ' or '\t' or ';' or '|' or '\n'))
                return true;
        }
        return false;
    }

    private static bool IsBashCommandTrusted(ClassifiedCommand classified, string cwd)
    {
        // Only auto-approve certain categories
        if (classified.Category is not (CommandCategory.Read or CommandCategory.Write or CommandCategory.Copy or CommandCategory.Run))
            return false;

        // For Run commands with no specific path (e.g. "dotnet build", "git status"), trust them
        // For commands with identifiable paths, check sandbox
        var displayText = classified.DisplayText;

        // If it's a Run category, the display text is the full command - these are typically
        // in-cwd commands like "dotnet build", "npm install", "git status" etc.
        if (classified.Category == CommandCategory.Run)
            return true; // Non-dangerous Run commands are already filtered by the caller

        // For Read/Write/Copy with a path in display text, check the sandbox
        if (!string.IsNullOrEmpty(displayText) && displayText != classified.DisplayText)
            return TrustSandbox.IsPathTrustedRelative(displayText, cwd);

        // For Read with a path
        if (classified.Category == CommandCategory.Read)
            return TrustSandbox.IsPathTrustedRelative(displayText, cwd);

        // For Write with a path
        if (classified.Category == CommandCategory.Write)
            return TrustSandbox.IsPathTrustedRelative(displayText, cwd);

        // For Copy, the display text is "src → dst"
        if (classified.Category == CommandCategory.Copy && displayText.Contains(" → "))
        {
            var parts = displayText.Split(" → ");
            return parts.All(p => TrustSandbox.IsPathTrustedRelative(p.Trim(), cwd));
        }

        return true;
    }
}
