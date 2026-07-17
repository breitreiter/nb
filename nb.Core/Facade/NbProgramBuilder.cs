using Microsoft.Extensions.Configuration;
using nb.Transcript;

namespace nb;

/// <summary>
/// A fluent way to author a conversation program in C# — the same event stream the
/// CLI parses from a <c>.nb</c> file, built by method calls instead. Each method
/// appends one directive; <see cref="Build"/> (or passing the builder straight to
/// <see cref="RunAsync"/>) yields the event list. Config directives set the envelope
/// going forward, message directives append turns, and <see cref="Run"/> invokes the
/// model on the accumulated state.
/// </summary>
public sealed class NbProgramBuilder
{
    private readonly List<TranscriptEvent> _events = new();

    public NbProgramBuilder Provider(string name) { _events.Add(new ProviderEvent { Name = name }); return this; }
    public NbProgramBuilder Model(string name) { _events.Add(new ModelEvent { Name = name }); return this; }
    public NbProgramBuilder System(string text) { _events.Add(new SystemEvent { Text = text }); return this; }
    public NbProgramBuilder User(string text) { _events.Add(new UserEvent { Text = text }); return this; }
    public NbProgramBuilder Assistant(string text) { _events.Add(new AssistantTextEvent { Text = text }); return this; }

    /// <summary>Append any pre-built events (e.g. a loaded seed transcript).</summary>
    public NbProgramBuilder Add(IEnumerable<TranscriptEvent> events) { _events.AddRange(events); return this; }

    /// <summary>Invoke the model on the accumulated state; <paramref name="prompt"/> is an optional trailing user turn.</summary>
    public NbProgramBuilder Run(string? prompt = null) { _events.Add(new RunEvent { Prompt = prompt }); return this; }

    public IReadOnlyList<TranscriptEvent> Build() => _events;

    /// <summary>Build and run in one step.</summary>
    public Task<RunResult> RunAsync(IConfiguration config, NbOptions? options = null, CancellationToken cancellationToken = default)
        => Nb.RunAsync(config, _events, options, cancellationToken);
}
