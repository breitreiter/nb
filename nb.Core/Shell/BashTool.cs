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

    public BashTool(
        ShellEnvironment env,
        int defaultTimeoutSeconds = 30,
        int outputThresholdLines = 200,
        int outputThresholdBytes = 10240,
        int sandwichHeadLines = 50,
        int sandwichTailLines = 20)
    {
        _env = env;
        _defaultTimeoutSeconds = defaultTimeoutSeconds;
        _outputThresholdLines = outputThresholdLines;
        _outputThresholdBytes = outputThresholdBytes;
        _sandwichHeadLines = sandwichHeadLines;
        _sandwichTailLines = sandwichTailLines;
    }

    /// <summary>
    /// The approval policy, read live at execute time for the bash <see cref="SandboxMode"/>.
    /// Injected by Program after the policy is built (Phase 5.3); a mid-program
    /// <c>approval sandbox</c> directive mutates it, so the sandbox mode is read per call.
    /// </summary>
    public ApprovalPolicy? ApprovalPolicy { get; set; }

    public string GetCwd() => _env.ShellCwd;

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

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var totalBytes = 0;
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

            stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, cts.Token);
            stderrTask = ReadLinesAsync(process.StandardError, stderrLines, cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask);
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

        // Calculate total bytes
        totalBytes = stdoutLines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1) +
                     stderrLines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1);

        // Apply sandwich truncation if needed
        var (stdout, stdoutTruncated) = ApplySandwich(stdoutLines);
        var (stderr, stderrTruncated) = ApplySandwich(stderrLines);
        truncated = stdoutTruncated || stderrTruncated;

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

    private async Task ReadLinesAsync(StreamReader reader, List<string> lines, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                lines.Add(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on timeout
        }
    }

    private (string result, bool truncated) ApplySandwich(List<string> lines)
    {
        var totalBytes = lines.Sum(l => Encoding.UTF8.GetByteCount(l) + 1);

        // Check if truncation is needed
        if (lines.Count <= _outputThresholdLines && totalBytes <= _outputThresholdBytes)
        {
            return (string.Join("\n", lines), false);
        }

        // Apply sandwich: head + omission message + tail
        var head = lines.Take(_sandwichHeadLines);
        var tail = lines.TakeLast(_sandwichTailLines);
        var omittedCount = lines.Count - _sandwichHeadLines - _sandwichTailLines;

        if (omittedCount <= 0)
        {
            // Not enough lines to sandwich, just return all
            return (string.Join("\n", lines), false);
        }

        var sb = new StringBuilder();
        sb.AppendJoin("\n", head);
        sb.AppendLine();
        sb.AppendLine($"\n[... {omittedCount} lines omitted ({FormatBytes(totalBytes)} total) - use grep/tail/head to filter ...]\n");
        sb.AppendJoin("\n", tail);

        return (sb.ToString(), true);
    }

    private static string FormatBytes(int bytes)
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
