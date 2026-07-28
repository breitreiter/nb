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
    public const string MaxToolCalls = "max_tool_calls";      // 3: turn aborted — tool-call budget exhausted
    public const string ToolErrorLimit = "tool_error_limit";  // 3: turn aborted — a tool failed repeatedly
    public const string TokenBudget = "token_budget";         // 3: run aborted — token budget exhausted
    public const string TimeBudget = "time_budget";           // 3: run aborted — wall-clock budget exhausted
    public const string ApprovalDenied = "approval_denied";   // 4: approval required but policy denied

    public static int ToExitCode(string reason) => reason switch
    {
        Ok => 0,
        ProviderError => 2,
        MaxToolCalls or ToolErrorLimit or TokenBudget or TimeBudget => 3,
        ApprovalDenied => 4,
        _ => 0,
    };
}
