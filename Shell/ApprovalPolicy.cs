namespace nb.Shell;

/// <summary>Whether a tool call auto-approves, needs a prompt, or is refused.</summary>
public enum ApprovalDecision { Allow, Prompt, Deny }

/// <summary>
/// The resolved effect of <c>--approve</c> / <c>--trust</c> / <c>alwaysAllow</c>:
/// which tool calls auto-approve and which fall to a prompt. One object owns the
/// two open-coded decision chains (bash and MCP) so Phase 5.2 can drive them from
/// an <c>Approval</c> config block and 5.3 can carry the bash sandbox mode. See
/// plans/approval-policy-and-sandbox.md. The interactive prompt UX and the
/// non-TTY deny stay at the call site; the policy only chooses the decision.
/// </summary>
public sealed class ApprovalPolicy
{
    private readonly bool _trust;
    private readonly ApprovalPatterns _bashPatterns;      // --approve
    private readonly Func<string, bool> _mcpAlwaysAllowed; // McpManager.IsAlwaysAllowed

    public ApprovalPolicy(bool trust, ApprovalPatterns bashPatterns, Func<string, bool> mcpAlwaysAllowed)
    {
        _trust = trust;
        _bashPatterns = bashPatterns;
        _mcpAlwaysAllowed = mcpAlwaysAllowed;
    }

    public bool TrustMode => _trust;

    /// <summary>
    /// Bash precedence: <c>--approve</c> match → safe-command allowlist
    /// (non-dangerous) → trust+sandbox → else prompt. <c>Reason</c> labels an
    /// <see cref="ApprovalDecision.Allow"/> so the caller preserves its log line
    /// (pre-approved / safe / trust). Deny is the caller's non-TTY collapse.
    /// </summary>
    public (ApprovalDecision Decision, string? Reason) DecideBash(string command, ClassifiedCommand classified, string cwd, bool bashPresent)
    {
        if (_bashPatterns.IsApproved(command))
            return (ApprovalDecision.Allow, "pre-approved");

        if (!classified.IsDangerous && IsSafeCommand(command))
            return (ApprovalDecision.Allow, "safe");

        if (_trust && !classified.IsDangerous && bashPresent && IsBashCommandTrusted(classified, cwd))
            return (ApprovalDecision.Allow, "trust");

        return (ApprovalDecision.Prompt, null);
    }

    /// <summary>MCP: an <c>alwaysAllow</c>-listed tool auto-approves, else prompts.</summary>
    public ApprovalDecision DecideMcp(string compositeToolName) =>
        _mcpAlwaysAllowed(compositeToolName) ? ApprovalDecision.Allow : ApprovalDecision.Prompt;

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
