using System.Diagnostics;
using System.Runtime.InteropServices;

namespace nb.Shell;

public class ShellEnvironment
{
    // Immutable - where nb was launched, where history lives
    public string LaunchDirectory { get; }

    // Mutable - where shell commands execute
    public string ShellCwd { get; private set; }

    // Static context detected at startup
    public string OS { get; }
    public string OSVersion { get; }
    public string ShellPath { get; }
    public string ShellName { get; }
    public string Architecture { get; }
    public string Username { get; }
    public string HomeDirectory { get; }
    public bool CaseSensitiveFs { get; }
    public HashSet<string> AvailableTools { get; }
    public HashSet<string> MissingTools { get; }

    private ShellEnvironment(
        string launchDirectory,
        string os,
        string osVersion,
        string shellPath,
        string shellName,
        string architecture,
        string username,
        string homeDirectory,
        bool caseSensitiveFs,
        HashSet<string> availableTools,
        HashSet<string> missingTools)
    {
        LaunchDirectory = launchDirectory;
        ShellCwd = launchDirectory;
        OS = os;
        OSVersion = osVersion;
        ShellPath = shellPath;
        ShellName = shellName;
        Architecture = architecture;
        Username = username;
        HomeDirectory = homeDirectory;
        CaseSensitiveFs = caseSensitiveFs;
        AvailableTools = availableTools;
        MissingTools = missingTools;
    }

    public static ShellEnvironment Detect(IEnumerable<string>? toolsToDetect = null)
    {
        var launchDir = Directory.GetCurrentDirectory();
        var (os, osVersion) = DetectOS();
        var (shellPath, shellName) = DetectShell();
        var architecture = DetectArchitecture();
        var username = Environment.UserName;
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var caseSensitive = DetectCaseSensitivity();

        var defaultTools = new[]
        {
            "python3", "python", "node", "dotnet", "git", "docker",
            "curl", "jq", "ffmpeg", "magick", "kubectl", "aws", "az"
        };

        var toolList = toolsToDetect ?? defaultTools;
        var (available, missing) = DetectTools(toolList);

        return new ShellEnvironment(
            launchDir, os, osVersion, shellPath, shellName,
            architecture, username, homeDir, caseSensitive,
            available, missing);
    }

    public void SetCwd(string path)
    {
        var resolved = Path.GetFullPath(path, ShellCwd);
        if (!Directory.Exists(resolved))
            throw new DirectoryNotFoundException($"Directory not found: {resolved}");
        ShellCwd = resolved;
    }

    public string BuildSystemPromptSection()
    {
        var availableList = AvailableTools.Count > 0
            ? string.Join(", ", AvailableTools.OrderBy(t => t))
            : "none detected";
        var missingList = MissingTools.Count > 0
            ? string.Join(", ", MissingTools.OrderBy(t => t))
            : "none";

        return $"""
## Environment
- OS: {OS} ({OSVersion})
- Shell: {ShellName}
- Architecture: {Architecture}
- User: {Username}
- Home: {HomeDirectory}
- Working directory: {ShellCwd}
- Case-sensitive filesystem: {(CaseSensitiveFs ? "yes" : "no")}

## Available Tools
Present: {availableList}
Not found: {missingList}

## Bash Tool
You have access to a `bash` tool to execute shell commands. Each command requires user approval unless pre-approved via --approve flag.
Commands execute in: {ShellCwd}
""";
    }

    private static (string os, string version) DetectOS()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return ("Windows", Environment.OSVersion.VersionString);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var version = TryRunCommand("sw_vers", "-productVersion") ?? "unknown";
            return ("macOS", version.Trim());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Try to get distro info from /etc/os-release
            var version = "unknown";
            if (File.Exists("/etc/os-release"))
            {
                var lines = File.ReadAllLines("/etc/os-release");
                var prettyName = lines.FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                if (prettyName != null)
                {
                    version = prettyName.Split('=')[1].Trim('"');
                }
            }
            return ("Linux", version);
        }

        return ("Unknown", Environment.OSVersion.VersionString);
    }

    private static (string path, string name) DetectShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Default to PowerShell on Windows
            var pwsh = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";

            // Prefer PowerShell if available
            var pwshPath = TryFindCommand("pwsh") ?? TryFindCommand("powershell");
            if (pwshPath != null)
                return (pwshPath, "PowerShell");

            return (pwsh, "cmd");
        }

        // Unix: use SHELL env var
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";
        var shellName = Path.GetFileName(shell);
        return (shell, shellName);
    }

    private static string DetectArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString()
        };
    }

    private static bool DetectCaseSensitivity()
    {
        // Windows and macOS are typically case-insensitive
        // Linux is typically case-sensitive
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return false;

        return true; // Linux default
    }

    private static (HashSet<string> available, HashSet<string> missing) DetectTools(IEnumerable<string> tools)
    {
        var available = new HashSet<string>();
        var missing = new HashSet<string>();

        foreach (var tool in tools)
        {
            if (TryFindCommand(tool) != null)
                available.Add(tool);
            else
                missing.Add(tool);
        }

        return (available, missing);
    }

    private static string? TryFindCommand(string command)
    {
        var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which";
        var result = TryRunCommand(whichCmd, command);
        return string.IsNullOrWhiteSpace(result) ? null : result.Split('\n')[0].Trim();
    }

    private static string? TryRunCommand(string command, string args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
