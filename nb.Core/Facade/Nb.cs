using Microsoft.Extensions.Configuration;
using Spectre.Console;
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
    /// reason. Throws <see cref="NbStartupException"/> if the engine can't be built and
    /// <see cref="TranscriptFormatException"/> if the program is malformed; run
    /// *outcomes* (provider_error, approval_denied, …) are carried in the result, never
    /// thrown. Engine chrome goes to <c>options.DiagnosticsWriter</c> (suppressed by default).
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

            var warnings = new List<string>();
            var evaluator = new ProgramEvaluator(runtime.Conversation, runtime.ClientFactory, warnings);
            await evaluator.EvaluateAsync(program);

            var events = TranscriptMapper.FromHistory(runtime.Conversation.History);
            UsageInfo? usage = runtime.Conversation.TotalUsage is { } u
                ? new UsageInfo { Input = u.input, Output = u.output, Total = u.total }
                : null;
            var reason = runtime.Conversation.LastOutcome;

            return new RunResult
            {
                Events = events,
                Answer = TranscriptMapper.LastAnswer(events),
                Usage = usage,
                ExitReason = reason,
                ExitCode = ExitReasons.ToExitCode(reason),
                OutputMode = evaluator.OutputMode,
                Warnings = warnings,
            };
        }
        finally
        {
            AnsiConsole.Console = savedConsole;
        }
    }
}
