using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Harness;
using nb.Transcript;

namespace nb;

/// <summary>
/// The in-process entry point: run a conversation program and get back a typed
/// <see cref="RunResult"/>. This is the library surface of the "one contract, three
/// surfaces" design — the same evaluator the CLI drives, with no engine types crossing
/// the boundary and no process-killing on failure. See
/// plans/composable-cli-reorientation.md (Pillar 5).
/// </summary>
public static class Nb
{
    /// <summary>Start a fluent program: <c>Nb.Program().Spec("headless").System("…").Run("do it")</c>.</summary>
    public static NbProgramBuilder Program() => new();

    /// <summary>
    /// Assemble a fresh engine from <paramref name="config"/>, evaluate
    /// <paramref name="program"/>, and return the transcript, answer, usage, and exit
    /// reason. Throws <see cref="NbStartupException"/> if the engine can't be built,
    /// <see cref="TranscriptFormatException"/> if the program is malformed, and
    /// <see cref="MCP.McpServerUnavailableException"/> if the program selects an MCP
    /// server (<c>mcp +name</c>) that failed to start; run *outcomes* (provider_error,
    /// approval_denied, …) are carried in the result, never thrown. A server that fails
    /// but is never selected is a non-fatal <see cref="RunResult.Warnings"/> entry.
    /// Engine chrome goes to <c>options.DiagnosticsWriter</c> (suppressed by default).
    /// </summary>
    public static async Task<RunResult> RunAsync(
        IConfiguration config,
        IReadOnlyList<TranscriptEvent> program,
        NbOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new NbOptions();

        // Engine classes still write via AnsiConsole (a 6b cleanup); redirect it to the
        // diagnostics sink so stdout stays clean for the caller. Global + restored.
        var savedConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(options.DiagnosticsWriter ?? TextWriter.Null),
        });
        try
        {
            using var runtime = await NbRuntime.BuildAsync(config, options);
            await runtime.Mcp.ConnectAllAsync();

            // A configured server that failed to connect is a non-fatal warning here;
            // it only hard-fails (below, during evaluation) if the program selects it.
            var warnings = new List<string>(runtime.StartupWarnings);
            foreach (var (name, error) in runtime.Mcp.FailedServers)
                warnings.Add($"MCP server '{name}' failed to start: {error}");

            var evaluator = new ProgramEvaluator(runtime.Conversation, runtime.ClientFactory, warnings);
            await evaluator.EvaluateAsync(program, cancellationToken);

            var events = TranscriptMapper.FromHistory(runtime.Conversation.History, runtime.Conversation.Approvals);
            var estimated = runtime.Conversation.UsageIsEstimated;
            UsageInfo? usage = runtime.Conversation.TotalUsage is { } u
                ? new UsageInfo { Input = u.input, Output = u.output, Total = u.total, Estimated = estimated }
                : null;
            if (estimated)
                warnings.Add("provider reported no token usage; the counts in Usage are estimated from " +
                             "message size (roughly ±30%), and any token budget was enforced against that estimate");
            var reason = runtime.Conversation.LastOutcome;

            return new RunResult
            {
                Events = events,
                Answer = TranscriptMapper.LastAnswer(events),
                Usage = usage,
                ExitReason = reason,
                ExitCode = ExitReasons.ToExitCode(reason),
                Harness = evaluator.Harness == HarnessRegistry.Default ? null : evaluator.Harness,
                Denied = runtime.Conversation.Approvals.DeniedCount,
                Warnings = warnings,
            };
        }
        finally
        {
            AnsiConsole.Console = savedConsole;
        }
    }
}
