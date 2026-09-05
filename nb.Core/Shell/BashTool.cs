using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;

namespace nb.Shell;

public record ShellResult(
    string Stdout,
    string Stderr,
    int ExitCode,
    bool Truncated,
    bool TimedOut);

public class BashTool
{
    private readonly ShellEnvironment _env;
    private readonly int _defaultTimeoutSeconds;
    private readonly int _outputThresholdLines;
    private readonly int _outputThresholdBytes;
    private readonly int _sandwichHeadLines;
    private readonly int _sandwichTailLines;
    private readonly long _outputByteCeiling;

    public BashTool(
        ShellEnvironment env,
        int defaultTimeoutSeconds = 30,
        int outputThresholdLines = 200,
        int outputThresholdBytes = 10240,
        int sandwichHeadLines = 50,
        int sandwichTailLines = 20,
        long outputByteCeiling = 8L * 1024 * 1024)
    {
        _env = env;
        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _outputThresholdLines = outputThresholdLines;
        _outputThresholdBytes = outputThresholdBytes;
        _sandwichHeadLines = sandwichHeadLines;
        _sandwichTailLines = sandwichTailLines;
        _outputByteCeiling = outputByteCeiling;
    }

    /// <summary>
    /// The approval policy, read live at execute time for the bash <see cref="SandboxMode"/>.
    /// Injected by Program after the policy is built (Phase 5.3); a mid-program
    /// <c>approval sandbox</c> directive mutates it, so the sandbox mode is read per call.
    /// </summary>
    public ApprovalPolicy? ApprovalPolicy { get; set; }

    public string GetCwd() => _env.ShellCwd;

    /// <summary>
    /// The detected shell environment. Exposed for harness costumes, which report OS,
    /// shell and cwd to the model in their target's own environment block.
    /// </summary>
    public ShellEnvironment Environment => _env;

    public AIFunction CreateTool()
    {
        var executeFunc = (string description, string command, int? timeout_seconds) =>
            ExecuteAsync(command, null, timeout_seconds);

        return AIFunctionFactory.Create(
            executeFunc,
            name: "bash",
            description: $"""
                Execute a shell command and return the output.
                Commands run in: {_env.ShellCwd}
                Shell: {_env.ShellName}

                Parameters:
                - description: Brief explanation (5-10 words) of what this command does and why. Required.
                - command: The shell command to execute.
                - timeout_seconds: Optional timeout (default {_defaultTimeoutSeconds}s).

                Returns stdout, stderr, and exit code. Large outputs are truncated.
                Commands require user approval before execution.
                """
        );
    }

    public async Task<ShellResult> ExecuteAsync(
        string command,
        string? cwd = null,
        int? timeoutSeconds = null)
    {
        var workingDir = cwd ?? _env.ShellCwd;
        var requested = timeoutSeconds ?? _defaultTimeoutSeconds;
        var timeout = Math.Min(requested, _defaultTimeoutSeconds);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        ConfigureCommand(psi, command, workingDir);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var process = new Process { StartInfo = psi };

        // One budget for the whole call, shared by both streams: a runaway producer is a
        // property of the command, not of which pipe it happens to be writing to.
        using var budget = new ByteBudget(_outputByteCeiling);
        var stdoutLines = new OutputCollector(RetainedHeadLines, _sandwichTailLines, budget);
        var stderrLines = new OutputCollector(RetainedHeadLines, _sandwichTailLines, budget);
        var truncated = false;
        var timedOut = false;

        Task? stdoutTask = null;
        Task? stderrTask = null;

        try
        {
            process.Start();
            // Trap this child and any descendants in nb's job object so MSBuild /
            // VBCSCompiler daemons don't outlive us on Windows.
            ProcessJob.Assign(process);

            // Readers stop on either the timeout or the byte ceiling; the process only ever
            // stops on the timeout, so `timedOut` stays a statement about the clock.
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, budget.Token);
            stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, reading.Token);
            stderrTask = ReadLinesAsync(process.StandardError, stderrLines, reading.Token);

            await Task.WhenAll(stdoutTask, stderrTask);
            if (budget.Exhausted)
            {
                // Nothing further will be read, so leaving the child running only burns the
                // rest of the timeout. Kill it and say so, rather than reporting a truncated
                // result that looks like the command finished.
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            if (stdoutTask != null && stderrTask != null)
            {
                try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            }
        }

        // Totals accumulated during the read, so they stay exact even though the lines
        // behind them were dropped as they arrived.
        var (stdout, stdoutTruncated) = ApplySandwich(stdoutLines);
        var (stderr, stderrTruncated) = ApplySandwich(stderrLines);
        truncated = stdoutTruncated || stderrTruncated;

        if (budget.Exhausted)
        {
            stdout += $"\n[Output limit reached at {FormatBytes(budget.Charged)} - process killed. " +
                      "Narrow the command's output (grep/head/tail) and try again.]";
            truncated = true;
        }

        // Add timeout message if needed
        if (timedOut)
        {
            stdout += $"\n[Killed - exceeded {timeout}s timeout]";
        }

        // Validate UTF-8 (check for binary output)
        if (!IsValidUtf8(stdout) || !IsValidUtf8(stderr))
        {
            return new ShellResult(
                "Error: Binary output detected. Use appropriate tools for binary files.",
                "",
                -1,
                false,
                false);
        }

        return new ShellResult(
            stdout.Trim(),
            stderr.Trim(),
            timedOut ? -1 : process.ExitCode,
            truncated,
            timedOut);
    }

    private static async Task ReadLinesAsync(StreamReader reader, OutputCollector lines, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                // Add returns false once the call's byte budget is spent: stop reading rather
                // than keep draining a producer whose output can no longer be reported.
                if (!lines.Add(line)) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
    }

    private (string result, bool truncated) ApplySandwich(OutputCollector lines)
    {
        var totalBytes = lines.TotalBytes;

        // Check if truncation is needed. Under the threshold every line is still in Head —
        // RetainedHeadLines is sized so that the whole untruncated case fits there.
        if (lines.TotalLines <= _outputThresholdLines && totalBytes <= _outputThresholdBytes)
        {
            return (string.Join("\n", lines.Head), false);
        }

        // Apply sandwich: head + omission message + tail. When the output stayed within the
        // retained head, Head holds everything and the tail comes off its end; past that the
        // ring is the only place the last lines still exist.
        var head = lines.Head.Take(_sandwichHeadLines);
        var tail = lines.TotalLines <= lines.HeadCapacity
            ? lines.Head.TakeLast(_sandwichTailLines)
            : lines.Tail;
        var omittedCount = lines.TotalLines - _sandwichHeadLines - _sandwichTailLines;

        if (omittedCount <= 0)
        {
            // Not enough lines to sandwich, just return all
            return (string.Join("\n", lines.Head), false);
        }

        var sb = new StringBuilder();
        sb.AppendJoin("\n", head);
        sb.AppendLine();
        sb.AppendLine($"\n[... {omittedCount} lines omitted ({FormatBytes(totalBytes)} total) - use grep/tail/head to filter ...]\n");
        sb.AppendJoin("\n", tail);

        return (sb.ToString(), true);
    }

    /// <summary>
    /// How many leading lines to retain. The untruncated case returns every line, so the head
    /// must be able to hold a whole under-threshold output; past that only the sandwich's head
    /// is ever read back.
    /// </summary>
    private int RetainedHeadLines => Math.Max(_outputThresholdLines, _sandwichHeadLines);

    /// <summary>
    /// A per-call output budget shared by stdout and stderr. Charged from two concurrent
    /// reader tasks, hence the interlocked add.
    /// </summary>
    private sealed class ByteBudget(long ceiling) : IDisposable
    {
        private readonly CancellationTokenSource _spent = new();
        private long _charged;

        public long Charged => Interlocked.Read(ref _charged);
        public bool Exhausted => Charged >= ceiling;

        /// <summary>
        /// Fires when the budget runs out. Both readers wait on it, because only one stream
        /// need be the runaway: the other is parked on a pipe that will not reach EOF while
        /// the child lives, so stopping just the noisy reader still waits out the timeout.
        /// </summary>
        public CancellationToken Token => _spent.Token;

        /// <summary>Charge <paramref name="bytes"/>; false once the ceiling is reached.</summary>
        public bool TryCharge(long bytes)
        {
            if (Interlocked.Add(ref _charged, bytes) < ceiling) return true;
            try { _spent.Cancel(); } catch (ObjectDisposedException) { }
            return false;
        }

        public void Dispose() => _spent.Dispose();
    }

    /// <summary>
    /// Collects a stream's output in fixed memory: the first <c>headCap</c> lines, the last
    /// <c>tailCap</c> in a ring, and running totals for everything else.
    ///
    /// <para>The result of a bash call can never exceed head + tail lines, so retaining the
    /// whole output to throw away all but ~70 lines of it made peak memory a function of what
    /// the child chose to emit — an unbounded quantity that the timeout does not bound, since
    /// the timeout limits duration and memory is the resource at risk
    /// (bugs/Bash_Buffers_Unbounded_Output_Before_Truncating.md). Totals accumulate as lines
    /// arrive, so the reported size stays exact even though the lines themselves are gone.</para>
    /// </summary>
    private sealed class OutputCollector(int headCap, int tailCap, ByteBudget budget)
    {
        private readonly List<string> _head = new();
        private readonly Queue<string> _tail = new();

        public int TotalLines { get; private set; }
        public long TotalBytes { get; private set; }
        public int HeadCapacity => headCap;
        public IReadOnlyList<string> Head => _head;
        public IEnumerable<string> Tail => _tail;

        /// <summary>Record a line. Returns false once the call's byte budget is spent.</summary>
        public bool Add(string line)
        {
            TotalLines++;
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            TotalBytes += bytes;

            if (_head.Count < headCap)
            {
                _head.Add(line);
            }
            else if (tailCap > 0)
            {
                _tail.Enqueue(line);
                if (_tail.Count > tailCap) _tail.Dequeue();
            }

            return budget.TryCharge(bytes);
        }
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
        };
    }

    // Point the process at the command, honoring the sandbox mode (Phase 5.3).
    private void ConfigureCommand(ProcessStartInfo psi, string command, string cwd)
    {
        if ((ApprovalPolicy?.Sandbox ?? SandboxMode.None) == SandboxMode.Bwrap)
        {
            // bwrap runs bash inside a namespace. ArgumentList passes the command
            // literally, so no bash-escaping (the sandbox flags and the -c payload
            // are separate argv entries).
            psi.FileName = "bwrap";
            foreach (var a in BwrapSandbox.BuildArgs(_env.ShellPath, command, cwd, ApprovalPolicy!.SandboxNet))
                psi.ArgumentList.Add(a);
            return;
        }

        // Unsandboxed: bash everywhere (Git Bash on Windows, native bash/zsh/sh on Unix).
        // ArgumentList, same as the bwrap path above — the command reaches bash as one
        // argv entry, byte-for-byte. The old `-c "{escaped}"` form had to escape $ and
        // backticks for its own wrapper quoting, which corrupted them everywhere bash
        // would not have interpolated anyway (inside single quotes, most of all), so
        // `awk '{print $1}'` and `grep 'foo$'` did the wrong thing. There is no correct
        // version of that escape: deciding what to expand requires parsing the shell
        // grammar, which is bash's job.
        psi.FileName = _env.ShellPath;
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);
    }

    private static bool IsValidUtf8(string text)
    {
        // Check for replacement characters which indicate invalid UTF-8
        return !text.Contains('\uFFFD');
    }
}
