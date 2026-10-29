using Microsoft.Extensions.Configuration;
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
        using var session = await NbSession.StartAsync(config, options);
        await session.EvaluateAsync(program, cancellationToken);
        return session.Result();
    }
}
