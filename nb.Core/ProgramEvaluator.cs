using Microsoft.Extensions.AI;
using nb.Harness;
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
    private readonly NbHarness _baseHarness;

    public string? Provider { get; private set; }
    public string? Model { get; private set; }

    /// <summary>The harness in effect — nb's own surface unless a <c>harness</c> directive says otherwise.</summary>
    public string Harness { get; private set; } = HarnessRegistry.Default;

    public ProgramEvaluator(ConversationManager conversation, Func<string?, string?, IChatClient?> clientFactory, IList<string>? warnings = null)
    {
        _conversation = conversation;
        // The runtime-wired surface every costume is built over.
        _baseHarness = conversation.Harness;
        _clientFactory = clientFactory;
        _warnings = warnings ?? new List<string>();
    }

    public async Task EvaluateAsync(IReadOnlyList<TranscriptEvent> program, CancellationToken cancellationToken = default)
    {
        foreach (var ev in program)
            await EvaluateEventAsync(ev, cancellationToken);

        // Trailing turns with no run after them still join the built conversation.
        FlushTurns();
    }

    /// <summary>
    /// Evaluate one directive against the running state, WITHOUT the end-of-program
    /// flush — so the REPL can drive the same evaluator one entered line at a time.
    /// A <c>run</c> flushes buffered turns and invokes; trailing turns are flushed by
    /// <see cref="EvaluateAsync"/> (or left to the session end for a REPL).
    /// </summary>
    public async Task EvaluateEventAsync(TranscriptEvent ev, CancellationToken cancellationToken = default)
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
            case HarnessEvent h:
                // Name validity is settled by the parser (and by the serializer's reader
                // for JSONL bytecode). A costume swaps what is advertised, over the same
                // tool instances the runtime wired.
                Harness = h.Name;
                ApplyHarness(h.Name);
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
                await _conversation.RunAsync(r.Prompt, cancellationToken);
                break;
            // ThinkingEvent / AssistantJsonEvent / ResultEvent: output-only, ignored on input.
        }
    }

    // Swap the harness, and surface what the costume knowingly does not reproduce, so a
    // behavioural diff against the real harness arrives with a suspect list rather than
    // sending someone hunting through the costume's source for what it quietly skips.
    private void ApplyHarness(string name)
    {
        var harness = HarnessRegistry.Create(name, _baseHarness);
        _conversation.SetHarness(harness);

        // A named harness brings its prompt and its context furniture — no second
        // directive for either. Both materialise as ordinary system messages rather than
        // special engine-held slots, so they round-trip through the transcript and a
        // --seed replay reproduces the run even if the costume, or the project's own
        // instruction file, has been edited since (plans/harness-emulation.md, "The
        // preamble arrives with the costume"). They go to the FRONT of the pending turns:
        // the costume speaks first and the program's own system directives get the last
        // word, which is how these harnesses layer project context onto their own prompts
        // anyway. The costume orders its own fragments — preamble, project instructions,
        // environment block — because the real harnesses disagree about that order.
        var leading = harness.LeadingContext();

        var turn = FirstPendingTurn();
        for (var i = 0; i < leading.Count; i++)
            _turnBuffer.Insert(i, new SystemEvent { Turn = turn, Text = leading[i] });

        foreach (var omission in harness.Omissions)
            _warnings.Add($"harness '{harness.Name}' does not reproduce — {omission}");
    }

    // Turn numbers must be non-decreasing within a flush batch, so a preamble joining the
    // front of the buffer takes the turn already at the front rather than assuming zero —
    // a harness named after the first run would otherwise flush an out-of-order batch.
    private int FirstPendingTurn() =>
        _turnBuffer.OfType<MessageEvent>().Select(e => e.Turn ?? 0).DefaultIfEmpty(0).Min();

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
            case "search":
                // search_web is a single capability with no argument worth matching,
                // so it grants as a flag rather than a pattern list.
                if (ap.Value.Equals("allow", StringComparison.OrdinalIgnoreCase))
                    policy.SetSearchAllowed(true);
                else if (ap.Value.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                    policy.SetSearchAllowed(false);
                else
                    _warnings.Add($"approval search '{ap.Value}' unknown (allow | prompt) — ignored");
                break;
            case "fetch":
                // Same shape as search: a single capability, granted as a flag.
                if (ap.Value.Equals("allow", StringComparison.OrdinalIgnoreCase))
                    policy.SetFetchAllowed(true);
                else if (ap.Value.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                    policy.SetFetchAllowed(false);
                else
                    _warnings.Add($"approval fetch '{ap.Value}' unknown (allow | prompt) — ignored");
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
                _warnings.Add($"approval key '{ap.Key}' unknown (bash | mcp | search | default | sandbox) — ignored");
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
            case "wall_ms":
                _conversation.SetWallBudget(bg.Value <= 0 ? null : bg.Value);
                break;
            default:
                _warnings.Add($"budget key '{bg.Key}' unknown (tokens | tool_calls | wall_ms) — ignored");
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
