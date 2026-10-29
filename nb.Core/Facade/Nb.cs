using Microsoft.Extensions.Configuration;
using nb.Providers;
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

    /// <summary>
    /// The oracle's judgement on one message, outside any run: the same prompt, verdict
    /// rules and temperature as the in-process loop, so <c>evals/oracle-bench</c> measures
    /// this judge too. For a host that runs the conversation itself and stands in for its
    /// user (proctor's simulated user over Harbor's bridge).
    /// <paramref name="provider"/> names the config entry that judges, as
    /// <c>oracle provider</c> does; null is the active entry. Throws
    /// <see cref="NbStartupException"/> if that entry can't be built. A provider failure
    /// on the call itself is thrown as it came, since there is no run to carry it.
    /// </summary>
    public static async Task<JudgeResult> JudgeAsync(
        IConfiguration config,
        string sheet,
        string message,
        string? provider = null,
        NbOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new NbOptions();
        var answers = AnswerSheet.Parse(sheet);
        var warnings = new List<string>();
        if (answers.Entries.Count == 0)
            warnings.Add("oracle: the answer sheet has no headed entries — nothing can be selected, so every ask will be a miss");

        // The in-process loop does not judge a turn that ended with no text, and the run ends ok.
        if (string.IsNullOrWhiteSpace(message))
            return new JudgeResult { Verdict = OracleVerdictKind.Done, Warnings = [.. warnings, "oracle: the message is empty, so there is nothing to judge — done"] };

        NbSession.RedirectConsole(options.DiagnosticsWriter);
        try
        {
            var label = provider ?? config["ActiveProvider"];
            var client = new ProviderManager(options.ProvidersDirectory ?? config["ProvidersPath"]).TryCreateChatClient(config, label)
                ?? throw new NbStartupException($"could not build a client for the judge's provider '{label}'. Check the configuration.");
            var temperature = ProviderConfigResolver.ResolveProviderFloat(config, label ?? "", "Temperature");

            var response = await client.GetResponseAsync(OracleResolver.BuildPrompt(answers, message), OracleResolver.Options(temperature), cancellationToken);
            var verdict = OracleResolver.ParseVerdict(response, answers, warnings);
            return new JudgeResult
            {
                Verdict = verdict.Kind,
                Keys = verdict.Keys,
                Answer = verdict.Kind == OracleVerdictKind.Hit ? answers.Compose(verdict.Keys) : null,
                Raw = verdict.Raw,
                Usage = response.Usage is { } u
                    ? new UsageInfo { Input = u.InputTokenCount, Output = u.OutputTokenCount, Total = u.TotalTokenCount }
                    : null,
                Warnings = warnings,
            };
        }
        finally { NbSession.RestoreConsole(); }
    }
}
