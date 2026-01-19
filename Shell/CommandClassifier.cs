using System.Text.RegularExpressions;

namespace nb.Shell;

public enum CommandCategory
{
    Read,
    Write,
    Append,
    Delete,
    Move,
    Copy,
    Run
}

public record ClassifiedCommand(
    CommandCategory Category,
    string DisplayText,
    bool IsDangerous,
    string? DangerReason);

public static partial class CommandClassifier
{
    private static readonly List<string> DefaultDangerPatterns = new()
    {
        @"\brm\s+-r",
        @"\brm\s+-rf",
        @"\brm\s+-fr",
        @"\bsudo\b",
        @"\bdd\b",
        @"\bchmod\s+777",
        @"\bchmod\s+-R",
        @"\bcurl\b.*\|\s*sh",
        @"\bcurl\b.*\|\s*bash",
        @"\bwget\b.*\|\s*sh",
        @"\bwget\b.*\|\s*bash",
        @"\bmkfs\b",
        @"\bfdisk\b",
        @">\s*/dev/(?!null)",  // exclude /dev/null which is harmless
        @">\s*/etc/",
        @">\s*/usr/",
        @">\s*/bin/",
        @">\s*/sbin/"
    };

    private static List<Regex> _dangerRegexes = DefaultDangerPatterns
        .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToList();

    public static void SetDangerPatterns(IEnumerable<string> patterns)
    {
        _dangerRegexes = patterns
            .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToList();
    }

    public static ClassifiedCommand Classify(string command)
    {
        var trimmed = command.Trim();
        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var isMultiLine = lines.Length > 1;

        // Check for dangerous patterns
        var (isDangerous, dangerReason) = CheckDangerous(trimmed);

        // For multi-line commands, always classify as Run
        if (isMultiLine)
        {
            var displayText = FormatMultiLine(lines);
            // Check if any line is a write/delete operation
            var hasWrite = lines.Any(l => IsWriteOperation(l));
            var hasDelete = lines.Any(l => IsDeleteOperation(l));

            if (!isDangerous && (hasWrite || hasDelete))
            {
                isDangerous = true;
                dangerReason = hasDelete ? "contains delete operations" : "contains write operations";
            }

            return new ClassifiedCommand(CommandCategory.Run, displayText, isDangerous, dangerReason);
        }

        // Single line classification
        var firstWord = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";

        // Read operations
        if (IsReadOperation(trimmed, out var readPath))
        {
            return new ClassifiedCommand(CommandCategory.Read, readPath ?? trimmed, isDangerous, dangerReason);
        }

        // Write operations (echo/cat > file)
        if (IsWriteRedirect(trimmed, out var writePath))
        {
            // /dev/null is harmless - it's just discarding output
            var isDevNull = writePath?.Equals("/dev/null", StringComparison.OrdinalIgnoreCase) ?? false;
            return new ClassifiedCommand(
                CommandCategory.Write,
                writePath ?? trimmed,
                !isDevNull && !isDangerous ? true : isDangerous,  // Writes are dangerous unless to /dev/null
                isDevNull ? null : (dangerReason ?? "writes to file"));
        }

        // Append operations (>> file)
        if (IsAppendRedirect(trimmed, out var appendPath))
        {
            return new ClassifiedCommand(
                CommandCategory.Append,
                appendPath ?? trimmed,
                true,
                dangerReason ?? "appends to file");
        }

        // Delete operations
        if (IsDeleteOperation(trimmed))
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var target = parts.Length > 1 ? string.Join(" ", parts.Skip(1).Where(p => !p.StartsWith('-'))) : trimmed;
            return new ClassifiedCommand(
                CommandCategory.Delete,
                target,
                true,
                dangerReason ?? "deletes files");
        }

        // Move operations
        if (firstWord == "mv")
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var src = parts[1];
                var dst = parts[^1];
                return new ClassifiedCommand(
                    CommandCategory.Move,
                    $"{src} -> {dst}",
                    true,
                    dangerReason ?? "moves files");
            }
        }

        // Copy operations
        if (firstWord == "cp")
        {
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var src = parts[^2];
                var dst = parts[^1];
                return new ClassifiedCommand(
                    CommandCategory.Copy,
                    $"{src} -> {dst}",
                    isDangerous,
                    dangerReason);
            }
        }

        // Default: Run
        return new ClassifiedCommand(CommandCategory.Run, trimmed, isDangerous, dangerReason);
    }

    private static bool IsReadOperation(string command, out string? path)
    {
        path = null;
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var cmd = parts[0];
        if (cmd is "cat" or "head" or "tail" or "less" or "more")
        {
            // Find the file path (skip flags)
            path = parts.Skip(1).FirstOrDefault(p => !p.StartsWith("-"));
            return path != null;
        }

        return false;
    }

    private static bool IsWriteOperation(string line)
    {
        return line.Contains(" > ") || line.Contains("\t>") || Regex.IsMatch(line, @">\s*\S");
    }

    private static bool IsDeleteOperation(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("rm ") || trimmed.StartsWith("rm\t") || trimmed == "rm";
    }

    private static bool IsWriteRedirect(string command, out string? path)
    {
        path = null;
        // Match patterns like: echo "foo" > /path/to/file
        var match = Regex.Match(command, @">\s*([^\s|&;]+)(?:\s|$)");
        if (match.Success && !command.Contains(">>"))
        {
            path = match.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static bool IsAppendRedirect(string command, out string? path)
    {
        path = null;
        var match = Regex.Match(command, @">>\s*([^\s|&;]+)(?:\s|$)");
        if (match.Success)
        {
            path = match.Groups[1].Value;
            return true;
        }
        return false;
    }

    private static (bool isDangerous, string? reason) CheckDangerous(string command)
    {
        foreach (var regex in _dangerRegexes)
        {
            if (regex.IsMatch(command))
            {
                // Extract a human-readable reason from the pattern
                var pattern = regex.ToString();
                var reason = pattern switch
                {
                    _ when pattern.Contains("rm") && pattern.Contains("-r") => "recursive delete",
                    _ when pattern.Contains("sudo") => "privilege escalation",
                    _ when pattern.Contains("dd") => "disk operations",
                    _ when pattern.Contains("chmod") => "permission changes",
                    _ when pattern.Contains("curl") || pattern.Contains("wget") => "pipe to shell",
                    _ when pattern.Contains("mkfs") || pattern.Contains("fdisk") => "disk formatting",
                    _ when pattern.Contains(">/dev") || pattern.Contains(">/etc") => "write to system path",
                    _ => "dangerous pattern detected"
                };
                return (true, reason);
            }
        }
        return (false, null);
    }

    private static string FormatMultiLine(string[] lines)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"({lines.Length} lines):");
        foreach (var line in lines)
        {
            sb.AppendLine($"  {line}");
        }
        return sb.ToString().TrimEnd();
    }
}
