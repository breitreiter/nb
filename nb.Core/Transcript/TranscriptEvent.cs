using System.Text.Json.Nodes;

namespace nb.Transcript;

/// <summary>
/// One event in nb's transcript schema — the single wire format for jsonl
/// output, seed input, /save export, and hooks. See plans/transcript-schema.md.
///
/// The event stream is the canonical form; a message array is a derived view.
/// Core events (<see cref="UserEvent"/>, <see cref="AssistantTextEvent"/>,
/// <see cref="SystemEvent"/>, <see cref="ToolCallEvent"/>,
/// <see cref="ToolResultEvent"/>) round-trip losslessly. Enrichment
/// (<see cref="ThinkingEvent"/>, <see cref="AssistantJsonEvent"/>,
/// <see cref="ResultEvent"/>, and the <c>approved</c>/<c>result</c> fields) is
/// emitted on output and ignored on seed-load: output is a superset, seed-load
/// reads the subset it needs.
/// </summary>
public abstract record TranscriptEvent
{
    /// <summary>Wire discriminator (the <c>"type"</c> field). Pinned by each subtype.</summary>
    public abstract string Type { get; }

    /// <summary>
    /// Monotonic per-round counter shared by every event of one assistant round,
    /// so a loader re-batches a turn's <see cref="ToolCallEvent"/>s into one
    /// assistant message and its <see cref="ToolResultEvent"/>s into the
    /// following tool message. Null on run-level events (the <see cref="ResultEvent"/>
    /// trailer) and on a bare <see cref="RunEvent"/>.
    /// </summary>
    public int? Turn { get; init; }
}

/// <summary>A role-tagged message carrying either flat <see cref="Text"/> or multipart <see cref="Content"/>.</summary>
public abstract record MessageEvent : TranscriptEvent
{
    /// <summary>Flat text content — the common case. Mutually exclusive with <see cref="Content"/>.</summary>
    public string? Text { get; init; }

    /// <summary>Multipart content (text + image parts). Emitted when a turn is multimodal.</summary>
    public IReadOnlyList<ContentPart>? Content { get; init; }
}

public sealed record UserEvent : MessageEvent
{
    public override string Type => "user";
}

public sealed record AssistantTextEvent : MessageEvent
{
    public override string Type => "assistant_text";
}

/// <summary>A plain system-role message. Round-trips like any other message — "the system prompt" is not special-cased.</summary>
public sealed record SystemEvent : MessageEvent
{
    public override string Type => "system";
}

/// <summary>Model reasoning. Output-only: reaches the stream for a debugging human, discarded on seed-load (must not re-enter model view).</summary>
public sealed record ThinkingEvent : TranscriptEvent
{
    public override string Type => "thinking";
    public string Text { get; init; } = "";
}

public sealed record ToolCallEvent : TranscriptEvent
{
    public override string Type => "tool_call";

    /// <summary>The only join key between a call and its result. Must survive every representation.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Call arguments with original JSON types preserved (numbers stay numbers, bools stay bools).</summary>
    public JsonObject? Arguments { get; init; }

    /// <summary>
    /// Enrichment: whether the call was authorized — <c>allow</c> or <c>deny</c>. Ignored on
    /// seed-load. Instrumented live (history does not carry it) and passed into the mapper,
    /// the same route <see cref="UsageInfo"/> takes to the trailer.
    ///
    /// A refusal is the load-bearing observation here: once nothing prompts, a denial is the
    /// only non-allow outcome, and a transcript that does not record it cannot be compared
    /// against one that does. See plans/approval-without-prompts.md.
    /// </summary>
    public string? Approved { get; init; }

    /// <summary>
    /// Enrichment: which rung of the approval ladder decided <see cref="Approved"/> —
    /// <c>pre-approved</c>, <c>safe</c>, <c>trust</c>, <c>default-deny</c>, <c>no-match</c>.
    /// Ignored on seed-load. Separate from the verdict because "allowed by the safe list"
    /// and "allowed because the run was trusted" are different experiments.
    /// </summary>
    public string? ApprovalReason { get; init; }
}

public sealed record ToolResultEvent : TranscriptEvent
{
    public override string Type => "tool_result";

    public required string Id { get; init; }

    /// <summary>The exact model-facing payload — the one thing seed-load consumes and that must round-trip byte-for-byte.</summary>
    public required string Output { get; init; }

    /// <summary>Enrichment: structured mirror of <see cref="Output"/> (exit_code, entries, truncated, …). Ignored on seed-load.</summary>
    public JsonObject? Result { get; init; }
}

/// <summary>Enrichment: the parsed final <c>```json</c> fence, a convenience over the canonical <see cref="AssistantTextEvent"/>. Ignored on seed-load.</summary>
public sealed record AssistantJsonEvent : TranscriptEvent
{
    public override string Type => "assistant_json";
    public JsonNode? Value { get; init; }
}

/// <summary>
/// The sole invocation directive: sends the accumulated conversation to the
/// model. In a program (input) it marks where inference happens; in a recorded
/// transcript (output) a past run appears as the assistant result it produced,
/// so this event is an input-side directive. <see cref="Prompt"/> carries the
/// inline-prompt sugar (<c>run &lt;text&gt;</c> = <c>user &lt;text&gt;</c> then <c>run</c>).
/// </summary>
public sealed record RunEvent : TranscriptEvent
{
    public override string Type => "run";
    public string? Prompt { get; init; }
}

// ---- Config directives -----------------------------------------------------
// Set the envelope going forward; order matters (a directive governs every
// directive after it until overridden). Run-level, not conversation messages —
// Turn is null. See plans/conversation-program-evaluator.md ("The program model").

/// <summary>Select the active provider for subsequent runs.</summary>
public sealed record ProviderEvent : TranscriptEvent
{
    public override string Type => "provider";
    public required string Name { get; init; }
}

/// <summary>Select the model for subsequent runs.</summary>
public sealed record ModelEvent : TranscriptEvent
{
    public override string Type => "model";
    public required string Name { get; init; }
}

/// <summary>
/// Select the harness whose tool surface, result formatting and prompt preamble the
/// run wears — nb's own by default, or a costume imitating another agent's harness.
/// See plans/harness-emulation.md; names come from <c>HarnessRegistry</c>.
/// </summary>
public sealed record HarnessEvent : TranscriptEvent
{
    public override string Type => "harness";
    public required string Name { get; init; }
}

/// <summary>
/// A tool-surface directive with delta semantics: <see cref="Add"/> /
/// <see cref="Remove"/> toggle named members and <see cref="Reset"/> (the source
/// <c>none</c> token) clears the surface. Presets establish a baseline; a program
/// layered after adds or drops without restating the set.
/// </summary>
public abstract record SurfaceDirectiveEvent : TranscriptEvent
{
    public IReadOnlyList<string> Add { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Remove { get; init; } = Array.Empty<string>();
    public bool Reset { get; init; }
}

/// <summary>Enable/disable MCP servers by name (baseline empty). <c>mcp +figma -tester</c>.</summary>
public sealed record McpEvent : SurfaceDirectiveEvent
{
    public override string Type => "mcp";
}

/// <summary>Enable/disable native tools by name (baseline all-on). <c>tools -bash</c>, <c>tools none</c>.</summary>
public sealed record ToolsEvent : SurfaceDirectiveEvent
{
    public override string Type => "tools";
}

/// <summary>
/// An approval-policy directive: <c>approval &lt;key&gt; &lt;value&gt;</c> where key is
/// <c>bash</c> (add an auto-approve command pattern), <c>mcp</c> (add an allow glob),
/// or <c>default</c> (<c>prompt</c> | <c>deny</c> for unmatched calls). Layers onto the
/// config-seeded policy (plans/approval-policy-and-sandbox.md).
/// </summary>
public sealed record ApprovalEvent : TranscriptEvent
{
    public override string Type => "approval";
    public required string Key { get; init; }
    public required string Value { get; init; }
}

/// <summary>
/// A doom-loop-detector directive: <c>loop &lt;n&gt;</c> sets the repetition threshold
/// (a soft <c>&lt;system_reminder&gt;</c> nudge fires after N repeated tool-call
/// sequences); <c>loop off</c> (<see cref="Enabled"/> false) disables the detector.
/// On by default. Run-level; governs subsequent runs.
/// </summary>
public sealed record LoopEvent : TranscriptEvent
{
    public override string Type => "loop";

    /// <summary>Whether the detector runs at all. <c>loop off</c> sets this false.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Repetitions before the nudge fires. Meaningful only when <see cref="Enabled"/>.</summary>
    public int Threshold { get; init; }
}

/// <summary>
/// A resource-budget directive: <c>budget &lt;key&gt; &lt;value&gt;</c> where key is
/// <c>tokens</c> (session-cumulative token ceiling — the run aborts with
/// <see cref="ExitReasons.TokenBudget"/> once total usage crosses it) or
/// <c>tool_calls</c> (a per-turn override of the tool-call cap). Run-level.
/// </summary>
public sealed record BudgetEvent : TranscriptEvent
{
    public override string Type => "budget";
    public required string Key { get; init; }
    public required long Value { get; init; }
}

/// <summary>Run-level trailer (Turn is null). Not a conversation message — carries telemetry and the exit reason. Ignored on seed-load.</summary>
public sealed record ResultEvent : TranscriptEvent
{
    public override string Type => "result";
    public string ExitReason { get; init; } = "ok";
    public UsageInfo? Usage { get; init; }
    public int? Turns { get; init; }
    public int? ToolCalls { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>
    /// The harness the run wore, when it was not nb's own. Omitted for the default, so a
    /// plain trailer stays byte-identical to before this field existed. Recorded for the
    /// same reason the model is: a corpus of runs spanning more than one costume cannot
    /// be interpreted without it.
    /// </summary>
    public string? Harness { get; init; }

    /// <summary>
    /// How many tool calls were refused. Omitted when zero, so a run that hit no denials
    /// keeps a trailer byte-identical to before this field existed.
    ///
    /// The run-level counterpart to <see cref="ToolCallEvent.Approved"/>: a reader
    /// scanning trailers across a corpus can see that a run was fighting its authorization
    /// envelope without replaying it call by call.
    /// </summary>
    public int? Denied { get; init; }
}

/// <summary>Token usage on the run-level <see cref="ResultEvent"/> trailer.</summary>
public sealed record UsageInfo
{
    public long? Input { get; init; }
    public long? Output { get; init; }
    public long? Total { get; init; }

    /// <summary>
    /// True when the counts are nb's own size estimate rather than the provider's report
    /// — the provider (or something between nb and it) sent no usage. Emitted as
    /// <c>"estimated": true</c> and omitted otherwise, so a measured trailer is
    /// byte-identical to before. Don't bill from an estimated trailer.
    /// </summary>
    public bool Estimated { get; init; }
}

/// <summary>One part of a multipart message body.</summary>
public abstract record ContentPart
{
    public abstract string Kind { get; }
}

public sealed record TextPart : ContentPart
{
    public override string Kind => "text";
    public string Text { get; init; } = "";
}

/// <summary>
/// An image part. Images don't fully round-trip in v1: <see cref="Data"/> (base64)
/// is emitted when present, but the durable stand-in is <see cref="Note"/> — the
/// text placeholder the model actually saw (e.g. "[Image loaded: x.png]").
/// </summary>
public sealed record ImagePart : ContentPart
{
    public override string Kind => "image";
    public string? MediaType { get; init; }
    public string? Data { get; init; }
    public string? Note { get; init; }
}
