using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Spectre.Console;
using nb.Harness;
using nb.Transcript;

namespace nb;

/// <summary>
/// One engine and one conversation, driven a program or a prompt at a time.
/// <see cref="Nb.RunAsync"/> is a session that evaluates one program and reports;
/// <c>nb --acp</c> is a session that evaluates the program's directives, then takes its
/// prompts from the protocol. Either way the record is the conversation's journal, and
/// <see cref="Result"/> reads it as a <see cref="RunResult"/> at any point between calls.
/// </summary>
public sealed class NbSession : IDisposable
{
    private readonly NbRuntime _runtime;
    private readonly ProgramEvaluator _evaluator;
    private readonly List<string> _warnings;
    private readonly List<TranscriptEvent> _program = new();
    private TimeSpan _busy;
    private bool _disposed;

    private NbSession(NbRuntime runtime, List<string> warnings)
    {
        _runtime = runtime;
        _warnings = warnings;
        _evaluator = new ProgramEvaluator(runtime.Conversation, runtime.ClientFactory, warnings, runtime.DefaultHarnessFor, runtime.TemperatureFor);
    }

    /// <summary>The run as it happens. Subscribe to <see cref="RunJournal.Written"/> to follow it live.</summary>
    public RunJournal Journal => _runtime.Conversation.Journal;

    /// <summary>
    /// Assemble an engine from <paramref name="config"/> and connect its MCP servers. Throws
    /// <see cref="NbStartupException"/> if the engine can't be built. A configured server
    /// that fails to connect is a warning, and only fatal if a program selects it.
    /// </summary>
    public static async Task<NbSession> StartAsync(IConfiguration config, NbOptions? options = null)
    {
        options ??= new NbOptions();
        RedirectConsole(options.DiagnosticsWriter);
        try
        {
            var runtime = await NbRuntime.BuildAsync(config, options);
            await runtime.Mcp.ConnectAllAsync();
            var warnings = runtime.Mcp.FailedServers.Select(f => $"MCP server '{f.Key}' failed to start: {f.Value}").ToList();
            return new NbSession(runtime, warnings);
        }
        catch
        {
            RestoreConsole();
            throw;
        }
    }

    /// <summary>
    /// Evaluate <paramref name="program"/> against the session: directives set it up, each
    /// <c>run</c> invokes the model. Throws as <see cref="Nb.RunAsync"/> documents; run
    /// outcomes land in <see cref="Result"/>.
    /// </summary>
    public Task EvaluateAsync(IReadOnlyList<TranscriptEvent> program, CancellationToken cancellationToken = default)
    {
        _program.AddRange(program);
        return Timed(() => _evaluator.EvaluateAsync(program, cancellationToken));
    }

    /// <summary>
    /// One prompt, as a <c>run</c> with that prompt would be: the same budgets, loop
    /// detection and oracle.
    /// </summary>
    public Task PromptAsync(ChatMessage prompt, CancellationToken cancellationToken = default) =>
        Timed(() => _evaluator.PromptAsync(prompt, cancellationToken));

    /// <summary>The session so far, as a run's result: what <see cref="Nb.RunAsync"/> returns at the end of its program.</summary>
    public RunResult Result()
    {
        var conversation = _runtime.Conversation;
        var journal = conversation.Journal;
        var events = journal.Events;
        var estimated = conversation.UsageIsEstimated;
        UsageInfo? usage = conversation.TotalUsage is { } u
            ? new UsageInfo { Input = u.input, Output = u.output, Total = u.total, Estimated = estimated }
            : null;
        var warnings = new List<string>(_warnings);
        if (estimated)
            warnings.Add("provider reported no token usage; the counts in Usage are estimated from " +
                         "message size (roughly ±30%), and any token budget was enforced against that estimate");
        var reason = conversation.LastOutcome;

        // Hash the *resolved* program, not source text: the facade never sees source.
        // @file includes are expanded at parse time and a --seed prefix is spliced
        // into this same list, so the serialized event list is what actually ran and
        // the source is only a recipe for it. Two runs of one program therefore hash
        // equal, and a program hashes the same as its expanded form.
        var programSha256 = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(TranscriptSerializer.Serialize(_program))));

        return new RunResult
        {
            Events = events,
            Journal = journal.Entries.ToList(),
            Answer = TranscriptMapper.LastAnswer(events),
            Usage = usage,
            ExitReason = reason,
            ExitCode = ExitReasons.ToExitCode(reason),
            Provider = conversation.GetCurrentProvider(),
            Harness = _evaluator.Harness == HarnessRegistry.Default ? null : _evaluator.Harness,
            Denied = conversation.Approvals.DeniedCount,
            OracleTurns = _evaluator.OracleTurnsUsed,
            OracleVerdict = _evaluator.OracleVerdict,
            Model = conversation.GetCurrentModel(),
            Cost = conversation.TotalCost,
            Duration = _busy,
            ProviderTime = _runtime.ProviderTime.Total,
            ProgramSha256 = programSha256,
            NbVersion = global::nb.NbVersion.Current,
            Warnings = warnings,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _runtime.Dispose();
        RestoreConsole();
    }

    // Wall time spent evaluating, which becomes duration_ms. Engine startup (config load,
    // plugin discovery, MCP connects: order 2s locally) is not the program's, and neither is
    // the host's time between calls (an ACP client's think time), which the wall-clock
    // budget does not count either.
    private async Task Timed(Func<Task> work)
    {
        var started = Stopwatch.GetTimestamp();
        try { await work(); }
        finally
        {
            _busy += Stopwatch.GetElapsedTime(started);
            _runtime.Conversation.PauseWallClock();
        }
    }

    // Engine classes still write via AnsiConsole (a 6b cleanup); redirect it to the
    // diagnostics sink so stdout stays clean for the caller.
    //
    // AnsiConsole.Console is process-global, so a naive save/restore pair races when a
    // host runs several sessions at once: two each save what the other set, and the last
    // one out restores a writer that belongs to a finished session — leaving the process
    // console permanently pointed at a dead sink. Refcount instead: the first session in
    // redirects, the last one out restores, and nobody restores a stale value.
    //
    // Residual, and not fixable here: concurrent hosts share the first one's
    // DiagnosticsWriter, because one global cannot serve two sinks. Routing each session's
    // diagnostics to its own writer needs the reporter seam that would stop engine
    // classes writing to a global at all (TODO.md, "engine chrome still lives in
    // nb.Core"). bugs/Concurrent_Runs_Collide_On_The_Global_Console.md
    internal static void RedirectConsole(TextWriter? sink)
    {
        lock (ConsoleRedirectLock)
        {
            if (_consoleRedirectDepth++ == 0)
            {
                _savedConsole = AnsiConsole.Console;
                AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
                {
                    Out = new AnsiConsoleOutput(sink ?? TextWriter.Null),
                });
            }
        }
    }

    internal static void RestoreConsole()
    {
        lock (ConsoleRedirectLock)
        {
            if (--_consoleRedirectDepth == 0 && _savedConsole is not null)
            {
                AnsiConsole.Console = _savedConsole;
                _savedConsole = null;
            }
        }
    }

    private static readonly object ConsoleRedirectLock = new();
    private static int _consoleRedirectDepth;
    private static IAnsiConsole? _savedConsole;
}
