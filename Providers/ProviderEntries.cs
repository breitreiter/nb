using Microsoft.Extensions.Configuration;

namespace nb.Providers;

/// <summary>
/// One entry in the <c>ChatProviders</c> array.
/// </summary>
/// <param name="Label">
/// The name the user selects with <c>ActiveProvider</c> or <c>/provider</c>. Free-form.
/// </param>
/// <param name="Implementation">
/// The <see cref="IChatClientProvider.Name"/> of the plugin that backs this entry.
/// Taken from the entry's <c>Provider</c> field, falling back to <paramref name="Label"/>.
/// </param>
public sealed record ProviderEntry(string Label, string Implementation, IConfigurationSection Config)
{
    /// <summary>True when the entry is labelled differently from the plugin behind it.</summary>
    public bool IsAliased => !string.Equals(Label, Implementation, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads <c>ChatProviders</c> entries and separates the entry's label from the provider
/// implementation behind it.
/// </summary>
/// <remarks>
/// These were one string for a long time, which meant the set of usable <c>Name</c> values
/// was fixed by whatever happened to be compiled into <c>providers/</c> — so one
/// implementation could never front two backends. That matters most for <c>LocalLlm</c>,
/// a generic OpenAI-compatible client where every local server is the same implementation
/// at a different port.
///
/// The optional <c>Provider</c> field names the implementation; <c>Name</c> is now just a
/// label. Entries omitting <c>Provider</c> behave exactly as before.
/// </remarks>
public static class ProviderEntries
{
    /// <summary>Entry field naming the provider implementation.</summary>
    public const string ImplementationKey = "Provider";

    /// <summary>Entry field holding the user-facing label.</summary>
    public const string LabelKey = "Name";

    public static List<ProviderEntry> ReadAll(IConfiguration config)
    {
        var entries = new List<ProviderEntry>();

        foreach (var section in config.GetSection("ChatProviders").GetChildren())
        {
            var label = section[LabelKey];
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var implementation = section[ImplementationKey];
            if (string.IsNullOrWhiteSpace(implementation))
                implementation = label;

            entries.Add(new ProviderEntry(label, implementation, section));
        }

        return entries;
    }

    public static ProviderEntry? Find(IEnumerable<ProviderEntry> entries, string label) =>
        entries.FirstOrDefault(e => string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Labels appearing more than once. Lookup takes the first match, so any later entry
    /// sharing a label is dead config — worth saying out loud rather than dropping silently.
    /// </summary>
    public static List<string> DuplicateLabels(IEnumerable<ProviderEntry> entries) =>
        entries
            .GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().Label)
            .ToList();
}
