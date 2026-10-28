using Microsoft.Extensions.Configuration;
using nb.Providers;

namespace nb.Harness;

/// <summary>
/// The names a <c>harness</c> directive or a config <c>Harness</c> may say. There is one
/// surface, nb's own, and it is the default: a program that names none runs on it.
///
/// The costumes that imitated other agents' harnesses (claude-code, codex, qwen-code)
/// retired on <see cref="RetiredOn"/> (plans/atif-and-acp.md stage 1, executing
/// plans/agent-neutral-runs.md item 5): the question "does this work in Claude Code?" has
/// one answer now, which is to run Claude Code, as a proctor arm. Their names are refused
/// with that redirect rather than as unknown, so a config written before the retirement
/// says what happened instead of claiming the name never existed.
/// </summary>
public static class HarnessRegistry
{
    public const string Default = "nb";

    public const string RetiredOn = "2026-10-28";

    private static readonly Dictionary<string, string> Retired = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-code"] = "Claude Code",
        ["codex"] = "Codex",
        ["qwen-code"] = "Qwen Code",
    };

    public static bool IsKnown(string name) => string.Equals(name, Default, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="name"/> can't be worn, or null when it can.</summary>
    public static string? Refusal(string name) => IsKnown(name) ? null
        : Retired.TryGetValue(name, out var product)
            ? $"harness {name.ToLowerInvariant()} was retired on {RetiredOn}. To test in {product}, run {product}: "
              + $"a proctor arm with \"agent\": \"{name.ToLowerInvariant()}\". To run this model on nb's own surface, "
              + $"write harness {Default}, or name no harness."
            : $"unknown harness '{name}'. The only harness is {Default}, which is the default.";

    /// <summary>
    /// The harness config names for a run whose program names none: the provider entry's
    /// <c>Harness</c>, else top-level <c>Harness</c>. Null when config is silent, which means nb.
    /// </summary>
    public static string? ConfiguredDefault(IConfiguration config, string? providerLabel)
    {
        var label = providerLabel ?? config["ActiveProvider"];
        var entry = label is null ? null : ProviderEntries.Find(ProviderEntries.ReadAll(config), label);
        var name = entry?.Config["Harness"];
        return string.IsNullOrWhiteSpace(name) ? config["Harness"] : name;
    }
}
