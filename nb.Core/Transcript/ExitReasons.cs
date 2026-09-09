namespace nb.Transcript;

/// <summary>
/// The <c>exit_reason</c> vocabulary carried on <see cref="ResultEvent"/> and the
/// run-level exit-code contract (Phase 0 of plans/composable-cli-reorientation.md).
///
/// exit_reason is the fine-grained string on the transcript trailer;
/// <see cref="ToExitCode"/> collapses it to the coarse process exit code a shell
/// sees in <c>$?</c>. The mapping is deliberately many-to-one: several abort
/// reasons share exit code 3.
///
/// Codes not represented here: 1 (startup/config error) is emitted directly by
/// Program.cs before any run produces a trailer; 5 (history-lock conflict) was
/// retired with the lock in Phase 3.
/// </summary>
public static class ExitReasons
{
    public const string Ok = "ok";                            // 0: final answer produced
    public const string ProviderError = "provider_error";     // 2: provider/model error mid-turn
    public const string RateLimited = "rate_limited";         // 3: provider throttled us; retries exhausted
    public const string MaxToolCalls = "max_tool_calls";      // 3: turn aborted — tool-call budget exhausted
    public const string ToolErrorLimit = "tool_error_limit";  // 3: turn aborted — a tool failed repeatedly
    public const string TokenBudget = "token_budget";         // 3: run aborted — token budget exhausted
    public const string TimeBudget = "time_budget";           // 3: run aborted — wall-clock budget exhausted
    public const string ApprovalDenied = "approval_denied";   // 4: approval required but policy denied
    public const string OracleBudget = "oracle_budget";       // 3: run aborted — oracle_turns exhausted

    /// <summary>
    /// The model clearly asked the user for information and the answer sheet had no
    /// entry for it. Exit code <b>0</b>: the run ended exactly as it would have without
    /// an oracle, and only the label differs — it is a maintenance signal (the sheet
    /// needs an entry, or the prompt produced a question nobody anticipated) rather
    /// than a failure. Listed explicitly in <see cref="ToExitCode"/> even though the
    /// <c>_ => 0</c> fallback would produce the same number, because a reason exiting 0
    /// by accident is indistinguishable from one exiting 0 by decision.
    /// </summary>
    public const string OracleMiss = "oracle_miss";           // 0: asked something the sheet does not answer

    public static int ToExitCode(string reason) => reason switch
    {
        Ok or OracleMiss => 0,
        ProviderError => 2,
        MaxToolCalls or ToolErrorLimit or TokenBudget or TimeBudget or RateLimited or OracleBudget => 3,
        ApprovalDenied => 4,
        _ => 0,
    };
}
