using Microsoft.Extensions.AI;
using nb.Shell;
using nb.Transcript;

namespace nb;

/// <summary>
/// Evaluates a conversation-program: an ordered stream of transcript events.
/// Config directives set the envelope going forward (and swap the chat client on
/// a provider/model change), turn directives append messages, and each
/// <c>run</c> invokes the model on the accumulated state. See
/// plans/conversation-program-evaluator.md.
///
/// v1 scope: provider/model/output config, mcp/tools surface directives,
/// system/user/assistant/tool_call/tool_result turns, and run (with mid-stream
/// provider/model swap). Message-bearing turns buffer and flush through
/// <see cref="TranscriptLoader.ToHistory"/> at each run (and at the end), so a
/// turn's assistant text + its tool calls batch into one assistant message and
/// its results into one tool message — exactly as a seed loads. tool_call/
/// tool_result are fabricated premise (author them in JSONL; source syntax has no
/// verb for them), not live invocations.
/// </summary>
public sealed class ProgramEvaluator
{
    private readonly ConversationManager _conversation;
    // (provider, model) -> a chat client, or null on failure. Either may be null
    // (fall back to the configured default). The factory owns config/model override.
    private readonly Func<string?, string?, IChatClient?> _clientFactory;
    private readonly IList<string> _warnings;
    // Surface directives seen so far; re-folded into a ToolSurface before each run
    // so mcp/tools deltas take effect (plans/tool-surface-directives.md).
    private readonly List<SurfaceDirectiveEvent> _surfaceDirectives = new();
    // Message-bearing events awaiting the next run (or the final flush). Batched
    // per turn by TranscriptLoader.ToHistory, the same path a seed takes.
    private readonly List<TranscriptEvent> _turnBuffer = new();

    public string? Provider { get; private set; }
    public string? Model { get; private set; }

    public ProgramEvaluator(ConversationManager conversation, Func<string?, string?, IChatClient?> clientFactory, IList<string>? warnings = null)
    {
        _conversation = conversation;
        _clientFactory = clientFactory;
        _warnings = warnings ?? new List<string>();
    }

    public async Task EvaluateAsync(IReadOnlyList<TranscriptEvent> program)
    {
        foreach (var ev in program)
            await EvaluateEventAsync(ev);

        // Trailing turns with no run after them still join the built conversation.
        FlushTurns();
    }

    /// <summary>
    /// Evaluate one directive against the running state, WITHOUT the end-of-program
    /// flush — so the REPL can drive the same evaluator one entered line at a time.
    /// A <c>run</c> flushes buffered turns and invokes; trailing turns are flushed by
    /// <see cref="EvaluateAsync"/> (or left to the session end for a REPL).
    /// </summary>
    public async Task EvaluateEventAsync(TranscriptEvent ev)
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
            case SurfaceDirectiveEvent sd:
                _surfaceDirectives.Add(sd);
                break;
            case ApprovalEvent ap:
                ApplyApproval(ap);
                break;
            case LoopEvent lp:
                _conversation.SetDoomLoop(lp.Enabled, lp.Threshold);
                break;
            case BudgetEvent bg:
                ApplyBudget(bg);
                break;
            case SystemEvent or UserEvent or AssistantTextEvent or ToolCallEvent or ToolResultEvent:
                // A message-bearing turn: buffer until the next run flushes it.
                _turnBuffer.Add(ev);
                break;
            case RunEvent r:
                FlushTurns();
                _conversation.SetToolSurface(ToolSurface.Fold(_surfaceDirectives, ConversationManager.NativeToolNames));
                await _conversation.RunAsync(r.Prompt);
                break;
            // ThinkingEvent / AssistantJsonEvent / ResultEvent: output-only, ignored on input.
        }
    }

    // Batch buffered turns into history via the same loader a seed uses, so a
    // turn's assistant text + tool calls become one assistant message and its
    // results one tool message. ToHistory validates tool_call/tool_result pairing
    // (a program's fabricated rounds must be complete before the run consuming them).
    private void FlushTurns()
    {
        if (_turnBuffer.Count == 0) return;
        _conversation.AppendHistory(TranscriptLoader.ToHistory(_turnBuffer));
        _turnBuffer.Clear();
    }

    // Layer an `approval` directive onto the config-seeded policy. Takes effect for
    // subsequent runs, like the other config directives (plans/approval-policy-and-sandbox.md).
    private void ApplyApproval(ApprovalEvent ap)
    {
        var policy = _conversation.ApprovalPolicy;
        switch (ap.Key)
        {
            case "bash":
                policy.AddBashPattern(ap.Value);
                break;
            case "mcp":
                policy.AddMcpGlob(ap.Value);
                break;
            case "default":
                if (ap.Value.Equals("deny", StringComparison.OrdinalIgnoreCase))
                    policy.SetDefault(ApprovalDefault.Deny);
                else if (ap.Value.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                    policy.SetDefault(ApprovalDefault.Prompt);
                else
                    _warnings.Add($"approval default '{ap.Value}' unknown (prompt | deny) — ignored");
                break;
            case "sandbox":
                // Requested-but-unavailable hard-fails the run (ratified), like a bad
                // config Sandbox value — caught by RunProgramAsync → exit 1.
                if (!BwrapSandbox.TryParse(ap.Value, out var mode, out var allowNet))
                    _warnings.Add($"approval sandbox '{ap.Value}' unknown (none | bwrap | bwrap-net) — ignored");
                else if (mode == SandboxMode.Bwrap && !BwrapSandbox.IsAvailable())
                    throw new SandboxUnavailableException("Sandbox 'bwrap' requested but bubblewrap (bwrap) is not available on this host (Linux + bwrap on PATH required).");
                else
                    policy.SetSandbox(mode, allowNet);
                break;
            default:
                _warnings.Add($"approval key '{ap.Key}' unknown (bash | mcp | default | sandbox) — ignored");
                break;
        }
    }

    // Layer a `budget` directive onto the running conversation (subsequent runs).
    private void ApplyBudget(BudgetEvent bg)
    {
        switch (bg.Key)
        {
            case "tokens":
                _conversation.SetTokenBudget(bg.Value <= 0 ? null : bg.Value);
                break;
            case "tool_calls":
                _conversation.SetMaxToolCalls((int)Math.Clamp(bg.Value, 0, int.MaxValue));
                break;
            default:
                _warnings.Add($"budget key '{bg.Key}' unknown (tokens | tool_calls) — ignored");
                break;
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
}
