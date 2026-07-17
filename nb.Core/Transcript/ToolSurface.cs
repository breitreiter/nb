namespace nb.Transcript;

/// <summary>
/// The resolved tool surface for a run: which native tools and which MCP servers
/// are exposed to the model. Folded from the <see cref="SurfaceDirectiveEvent"/>s
/// preceding a run (see <see cref="Fold"/>), consumed by both <c>--resolve</c>
/// (the inspector) and <see cref="ProgramEvaluator"/> (the runtime), so what is
/// printed is provably what runs. See plans/tool-surface-directives.md.
/// </summary>
public sealed record ToolSurface
{
    /// <summary>Allowed native tool names, or null when all native tools are on (the baseline).</summary>
    public IReadOnlySet<string>? NativeAllow { get; init; }

    /// <summary>Allowed MCP server names, or null when MCP is uncontrolled (all connected servers).</summary>
    public IReadOnlyList<string>? McpServers { get; init; }

    /// <summary>The default: all native tools on, MCP uncontrolled — today's bare-path behavior.</summary>
    public static ToolSurface All { get; } = new();

    public bool AllowsNative(string name) => NativeAllow is null || NativeAllow.Contains(name);

    /// <summary>
    /// Fold a directive stream into the surface in effect at the end of it. Native
    /// starts all-on (null); the first constraint (a reset or remove) materializes
    /// an allow-set seeded from <paramref name="allNativeTools"/>. MCP starts
    /// <b>strict-empty</b> (an empty allow-list): a program exposes no MCP tools
    /// unless it names servers with <c>mcp +server</c>. (The bare/-p path never
    /// folds — it keeps <see cref="All"/>, whose null <see cref="McpServers"/>
    /// means all-connected — so this strict baseline applies only to programs.)
    /// </summary>
    public static ToolSurface Fold(IEnumerable<SurfaceDirectiveEvent> directives, IReadOnlyCollection<string> allNativeTools)
    {
        HashSet<string>? native = null;      // null => all-on
        var mcp = new List<string>();        // empty => strict baseline, no MCP

        foreach (var d in directives)
        {
            switch (d)
            {
                case ToolsEvent t:
                    native ??= new HashSet<string>(allNativeTools, StringComparer.OrdinalIgnoreCase);
                    if (t.Reset) native.Clear();
                    foreach (var r in t.Remove) native.Remove(r);
                    foreach (var a in t.Add) native.Add(a);
                    break;
                case McpEvent m:
                    if (m.Reset) mcp.Clear();
                    foreach (var r in m.Remove) mcp.RemoveAll(s => string.Equals(s, r, StringComparison.OrdinalIgnoreCase));
                    foreach (var a in m.Add) if (!mcp.Contains(a, StringComparer.OrdinalIgnoreCase)) mcp.Add(a);
                    break;
            }
        }

        return new ToolSurface { NativeAllow = native, McpServers = mcp };
    }
}
