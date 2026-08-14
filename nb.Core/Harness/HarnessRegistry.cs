namespace nb.Harness;

/// <summary>
/// The named harnesses a program can select with the <c>harness</c> directive.
///
/// Today there is exactly one: <see cref="Default"/>, nb's own canonical surface.
/// Costumes that imitate another agent's harness (see plans/harness-emulation.md) get
/// added here as they land, and each one is a name plus the <see cref="NbHarness"/>
/// subclass behind it — deliberately a closed, in-tree set rather than a plugin point.
/// If you need something weird, write a class.
///
/// An unrecognised name is a hard error rather than a warning, because a run that
/// silently falls back to nb's surface while the program says <c>codex</c> produces
/// comparative numbers that mean nothing. That failure is worse than the missing run.
/// </summary>
public static class HarnessRegistry
{
    /// <summary>nb's own surface — what a program gets when it names no harness.</summary>
    public const string Default = "nb";

    public static IReadOnlyList<string> KnownNames { get; } = new[] { Default };

    public static bool IsKnown(string name) =>
        KnownNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The registry's own spelling of a known name, so transcripts record one casing.</summary>
    public static string Canonicalize(string name) =>
        KnownNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    public static string KnownNamesForError() => string.Join(", ", KnownNames);
}
