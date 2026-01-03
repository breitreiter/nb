using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public AIFunction CreateSetCwdTool()
    {
        var setCwdFunc = (string path) =>
        {
            try
            {
                _env.SetCwd(path);
                return $"Working directory changed to: {_env.ShellCwd}";
            }
            catch (DirectoryNotFoundException ex)
            {
                return $"Error: {ex.Message}";
            }
        };

        return AIFunctionFactory.Create(
            setCwdFunc,
            name: "set_cwd",
            description: "Change the working directory for subsequent bash commands. Does not require approval."
        );
    }

    public async Task<ShellResult> ExecuteAsync(
        string command,
        string? cwd = null,
        int? timeoutSeconds = null)
    {
        var workingDir = cwd ?? _env.ShellCwd;
        var timeout = timeoutSeconds ?? _defaultTimeoutSeconds;

        var (shellPath, shellArgs) = GetShellCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = shellPath,
            Arguments = shellArgs,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var process = new Process { StartInfo = psi };

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();
        var totalBytes = 0;
        var truncated = false;
        var timedOut = false;

        try
        {
            process.Start();

            // Read stdout and stderr concurrently
            var stdoutTask = ReadLinesAsync(process.StandardOutput, stdoutLines, cts.Token);
            var stderrTask = ReadLinesAsync(process.StandardError, stderrLines, cts.Token);

            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort kill
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
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line != null)
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

    private (string shellPath, string shellArgs) GetShellCommand(string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use PowerShell on Windows
            return ("powershell", $"-NoProfile -Command \"{EscapePowerShell(command)}\"");
        }

        // Unix: use the detected shell
        return (_env.ShellPath, $"-c \"{EscapeBash(command)}\"");
    }

    private static string EscapeBash(string command)
    {
        // Escape double quotes and backslashes for bash -c "..."
        return command
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("$", "\\$")
            .Replace("`", "\\`");
    }

    private static string EscapePowerShell(string command)
    {
        // Escape for PowerShell -Command "..."
        return command
            .Replace("\"", "`\"")
            .Replace("$", "`$");
    }

    private static bool IsValidUtf8(string text)
    {
        // Check for replacement characters which indicate invalid UTF-8
        return !text.Contains('\uFFFD');
    }
}
