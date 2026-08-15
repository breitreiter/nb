namespace nb.Harness;

/// <summary>
/// The named harnesses a program can select with the <c>harness</c> directive.
///
/// <see cref="Default"/> is nb's own canonical surface; costumes that imitate another
/// agent's harness (see plans/harness-emulation.md) are each a name plus the
/// <see cref="NbHarness"/> subclass behind it — deliberately a closed, in-tree set
/// rather than a plugin point. If you need something weird, write a class.
///
/// An unrecognised name is a hard error rather than a warning, because a run that
/// silently falls back to nb's surface while the program says <c>codex</c> produces
/// comparative numbers that mean nothing. That failure is worse than the missing run.
/// </summary>
public static class HarnessRegistry
{
    /// <summary>nb's own surface — what a program gets when it names no harness.</summary>
    public const string Default = "nb";

    public static IReadOnlyList<string> KnownNames { get; } =
        new[] { Default, QwenCodeHarness.HarnessName, CodexHarness.HarnessName, ClaudeCodeHarness.HarnessName };

    /// <summary>
    /// Build the harness a name selects, over the tool instances the runtime wired.
    /// A costume swaps what is advertised, never what is behind it — and it gets every
    /// tool the runtime built, not the subset nb's own surface happens to advertise.
    /// </summary>
    public static NbHarness Create(string name, NbHarness baseHarness)
    {
        if (string.Equals(name, QwenCodeHarness.HarnessName, StringComparison.OrdinalIgnoreCase))
            return new QwenCodeHarness(
                baseHarness.Bash, baseHarness.ReadFile, baseHarness.WriteFile, baseHarness.EditFile,
                baseHarness.FindFiles, baseHarness.Grep, baseHarness.ListDir, baseHarness.FetchUrl,
                baseHarness.SearchWeb, baseHarness.ApplyPatch);

        if (string.Equals(name, CodexHarness.HarnessName, StringComparison.OrdinalIgnoreCase))
            return new CodexHarness(
                baseHarness.Bash, baseHarness.ReadFile, baseHarness.WriteFile, baseHarness.EditFile,
                baseHarness.FindFiles, baseHarness.Grep, baseHarness.ListDir, baseHarness.FetchUrl,
                baseHarness.SearchWeb, baseHarness.ApplyPatch);

        if (string.Equals(name, ClaudeCodeHarness.HarnessName, StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeHarness(
                baseHarness.Bash, baseHarness.ReadFile, baseHarness.WriteFile, baseHarness.EditFile,
                baseHarness.FindFiles, baseHarness.Grep, baseHarness.ListDir, baseHarness.FetchUrl,
                baseHarness.SearchWeb, baseHarness.ApplyPatch);

        return baseHarness;
    }

    public static bool IsKnown(string name) =>
        KnownNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The registry's own spelling of a known name, so transcripts record one casing.</summary>
    public static string Canonicalize(string name) =>
        KnownNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    public static string KnownNamesForError() => string.Join(", ", KnownNames);
}
