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

    /// <summary>Enrichment: how the call was approved (auto | preapproved | prompted | rejected). Ignored on seed-load.</summary>
    public string? Approved { get; init; }
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

/// <summary>Run-level trailer (Turn is null). Not a conversation message — carries telemetry and the exit reason. Ignored on seed-load.</summary>
public sealed record ResultEvent : TranscriptEvent
{
    public override string Type => "result";
    public string ExitReason { get; init; } = "ok";
    public UsageInfo? Usage { get; init; }
    public int? Turns { get; init; }
    public int? ToolCalls { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>Token usage on the run-level <see cref="ResultEvent"/> trailer.</summary>
public sealed record UsageInfo
{
    public long? Input { get; init; }
    public long? Output { get; init; }
    public long? Total { get; init; }
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
