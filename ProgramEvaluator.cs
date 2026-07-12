using Microsoft.Extensions.AI;
using nb.Transcript;

namespace nb;

/// <summary>
/// Evaluates a conversation-program: an ordered stream of transcript events.
/// Config directives set the envelope going forward (and swap the chat client on
/// a provider/model change), turn directives append messages, and each
/// <c>run</c> invokes the model on the accumulated state. See
/// plans/conversation-program-evaluator.md.
///
/// v1 scope: provider/model/output config, system/user/assistant turns, and run
/// (with mid-stream provider/model swap). Deferred: mcp/tools effects (tracked in
/// the resolved envelope for --resolve, but the tool surface is unchanged) and
/// tool_call/tool_result turns (bytecode-only; a program that fabricates tool
/// rounds warns and skips them for now).
/// </summary>
public sealed class ProgramEvaluator
{
    private readonly ConversationManager _conversation;
    // (provider, model) -> a chat client, or null on failure. Either may be null
    // (fall back to the configured default). The factory owns config/model override.
    private readonly Func<string?, string?, IChatClient?> _clientFactory;
    private readonly IList<string> _warnings;

    public string? Provider { get; private set; }
    public string? Model { get; private set; }
    public string? OutputMode { get; private set; }

    public ProgramEvaluator(ConversationManager conversation, Func<string?, string?, IChatClient?> clientFactory, IList<string>? warnings = null)
    {
        _conversation = conversation;
        _clientFactory = clientFactory;
        _warnings = warnings ?? new List<string>();
    }

    public async Task EvaluateAsync(IReadOnlyList<TranscriptEvent> program)
    {
        foreach (var ev in program)
        {
            switch (ev)
            {
                case ProviderEvent p:
                    Provider = p.Name;
                    SwapClient();
                    break;
                case ModelEvent m:
                    Model = m.Name;
                    SwapClient();
                    break;
                case OutputEvent o:
                    OutputMode = o.Mode;
                    break;
                case McpEvent or ToolsEvent:
                    // Tracked for --resolve; the tool surface is not yet reshaped (Phase 3 follow-up).
                    break;
                case SystemEvent s:
                    _conversation.AppendHistory(new[] { new ChatMessage(ChatRole.System, Text(s)) });
                    break;
                case UserEvent u:
                    _conversation.AppendHistory(new[] { new ChatMessage(ChatRole.User, Text(u)) });
                    break;
                case AssistantTextEvent a:
                    _conversation.AppendHistory(new[] { new ChatMessage(ChatRole.Assistant, Text(a)) });
                    break;
                case RunEvent r:
                    await _conversation.RunAsync(r.Prompt);
                    break;
                case ToolCallEvent or ToolResultEvent:
                    _warnings.Add("tool_call/tool_result in a program are not yet evaluated — skipped");
                    break;
                // ThinkingEvent / AssistantJsonEvent / ResultEvent: output-only, ignored on input.
            }
        }
    }

    private void SwapClient()
    {
        var client = _clientFactory(Provider, Model);
        if (client is null)
        {
            _warnings.Add($"could not build a client for provider '{Provider ?? "(default)"}' model '{Model ?? "(default)"}'");
            return;
        }
        _conversation.SwitchProvider(client, Provider ?? _conversation.GetCurrentProvider());
    }

    private static string Text(MessageEvent m)
    {
        if (m.Text is not null) return m.Text;
        if (m.Content is { } parts)
            return string.Concat(parts.OfType<TextPart>().Select(p => p.Text));
        return "";
    }
}
