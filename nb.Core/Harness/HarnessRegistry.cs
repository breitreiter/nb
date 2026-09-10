using Microsoft.Extensions.Configuration;
using nb.Providers;

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
            return new QwenCodeHarness(baseHarness);

        if (string.Equals(name, CodexHarness.HarnessName, StringComparison.OrdinalIgnoreCase))
            return new CodexHarness(baseHarness);

        if (string.Equals(name, ClaudeCodeHarness.HarnessName, StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeHarness(baseHarness);

        return baseHarness;
    }

    public static bool IsKnown(string name) =>
        KnownNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The registry's own spelling of a known name, so transcripts record one casing.</summary>
    public static string Canonicalize(string name) =>
        KnownNames.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? name;

    public static string KnownNamesForError() => string.Join(", ", KnownNames);

    /// <summary>
    /// The harness config names for a run whose program names none: the provider entry's
    /// <c>Harness</c>, else top-level <c>Harness</c>. Null when config is silent, which
    /// the evaluator refuses — every run wears one on purpose. This is the sanctioned
    /// config-default from plans/harness-emulation.md: a declaration keyed by entry, never
    /// an inference from a model slug.
    /// </summary>
    public static string? ConfiguredDefault(IConfiguration config, string? providerLabel)
    {
        var label = providerLabel ?? config["ActiveProvider"];
        var entry = label is null ? null : ProviderEntries.Find(ProviderEntries.ReadAll(config), label);
        var name = entry?.Config["Harness"];
        return string.IsNullOrWhiteSpace(name) ? config["Harness"] : name;
    }

    /// <summary>
    /// Why a run with no harness is refused, and what to do about it. The pairing line is
    /// guidance, not enforcement: a costume with the wrong vendor's model is academically
    /// interesting and never what "let's test with claude code" means.
    /// </summary>
    public const string RequiredMessage =
        "no harness named. Every run wears one: add `harness <name>` to the program, or "
        + "`\"Harness\"` to the provider entry in appsettings.json (known: nb, qwen-code, codex, "
        + "claude-code). Write `harness nb` explicitly to run on nb's own bare surface. "
        + "A costume expects its own vendor's model: claude-code with an Anthropic model, "
        + "codex with an OpenAI model, qwen-code with a Qwen model.";
}
