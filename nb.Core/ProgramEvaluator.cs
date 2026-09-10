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
    private readonly Func<string?, string?>? _defaultHarness;
    private bool _harnessNamed;

    public string? Provider { get; private set; }
    public string? Model { get; private set; }

    /// <summary>The harness in effect — nb's own surface unless a <c>harness</c> directive says otherwise.</summary>
    public string Harness { get; private set; } = HarnessRegistry.Default;

    /// <summary>
    /// The answer sheet an <c>oracle</c> directive attached, or null when the program
    /// declared none (plans/oracle-resolver.md). With one attached, every run that ends
    /// <c>ok</c> is judged by <see cref="OracleResolver"/> and continues on a hit.
    /// </summary>
    public string? Oracle { get; private set; }

    /// <summary>How many times the oracle resolved a halt and continued the run.</summary>
    public int OracleTurnsUsed { get; private set; }

    private AnswerSheet? _sheet;

    /// <summary>
    /// How many times the oracle may resolve a halt and continue the run before the run
    /// ends with <see cref="ExitReasons.OracleBudget"/>. Modest by default rather than
    /// unlimited: vague resolution → re-ask → vague resolution is the obvious infinite.
    /// Set by <c>budget oracle_turns &lt;n&gt;</c>, n positive like every other budget.
    /// </summary>
    public long OracleTurns { get; private set; } = DefaultOracleTurns;

    private const long DefaultOracleTurns = 8;

    /// <param name="defaultHarness">
    /// The harness to wear when the program has named none by its first <c>run</c>, given
    /// the provider label in effect (null = the active one). Null, or a resolver returning
    /// null, means the run is refused: a harness is never assumed.
    /// </param>
    public ProgramEvaluator(ConversationManager conversation, Func<string?, string?, IChatClient?> clientFactory, IList<string>? warnings = null,
        Func<string?, string?>? defaultHarness = null)
    {
        _conversation = conversation;
        // The runtime-wired surface every costume is built over.
        _baseHarness = conversation.Harness;
        _clientFactory = clientFactory;
        _warnings = warnings ?? new List<string>();
        _defaultHarness = defaultHarness;
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
    /// flush — so a library host can drive the same evaluator one directive at a time.
    /// A <c>run</c> flushes buffered turns and invokes; trailing turns are flushed by
    /// <see cref="EvaluateAsync"/>.
    ///
    /// (This was the REPL's entry point until it was deleted 2026-09-09; it stays public
    /// for the incremental library host — plans/retire-the-repl.md, open question 3.)
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
                _harnessNamed = true;
                ApplyHarness(h.Name);
                break;
            case OracleEvent o:
                Oracle = o.Sheet;
                _sheet = AnswerSheet.Parse(o.Sheet);
                if (_sheet.Entries.Count == 0)
                    _warnings.Add("oracle: the answer sheet has no headed entries — nothing can be selected, so every ask will be a miss");
                _conversation.SetOracleAvailable(true);
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
                EnsureHarnessNamed();
                FlushTurns();
                _conversation.SetToolSurface(ToolSurface.Fold(_surfaceDirectives, ConversationManager.NativeToolNames));
                await _conversation.RunAsync(r.Prompt, cancellationToken);
                await ResolveWithOracleAsync(cancellationToken);
                break;
            // ThinkingEvent / AssistantJsonEvent / ResultEvent: output-only, ignored on input.
        }
    }

    // The oracle loop (plans/oracle-resolver.md, "Continuation rule"). After a run ends ok:
    // ask the oracle whether the model was clearly waiting on the user for something the
    // sheet covers. Continue ONLY on a confident hit; everything else ends the run. So a
    // false positive costs nothing — the run ends as it would have without an oracle —
    // and a false negative needs the oracle to miss a clear question that has an entry.
    //
    // A run that ends any other way (budget, error, denial) is not judged: it did not
    // halt on the user, and the reason it did halt is the more important one to keep.
    private async Task ResolveWithOracleAsync(CancellationToken cancellationToken)
    {
        if (_sheet is null) return;

        while (_conversation.LastOutcome == ExitReasons.Ok)
        {
            var last = _conversation.LastAssistantText;
            if (last.Length == 0) return;

            var reply = await _conversation.SideCallAsync(OracleResolver.BuildPrompt(_sheet, last), OracleResolver.Options(), cancellationToken);
            var verdict = OracleResolver.ParseVerdict(reply, _sheet, _warnings);

            switch (verdict.Kind)
            {
                case OracleVerdictKind.Done:
                    return;
                case OracleVerdictKind.Miss:
                    // The unanswered question is already in the transcript as the last
                    // assistant_text — never paper over the ask. Only the label changes.
                    _warnings.Add("oracle: the model asked for something the answer sheet does not cover — the run ended as oracle_miss");
                    _conversation.SetOutcome(ExitReasons.OracleMiss);
                    return;
            }

            if (OracleTurnsUsed >= OracleTurns)
            {
                _warnings.Add($"oracle: {OracleTurns} resolutions spent and the model asked again — the run ended as oracle_budget");
                _conversation.SetOutcome(ExitReasons.OracleBudget);
                return;
            }

            OracleTurnsUsed++;
            _conversation.AppendOracleAnswer(_sheet.Compose(verdict.Keys), verdict.Keys);
            await _conversation.RunAsync(null, cancellationToken);
        }
    }

    // A run wears a harness on purpose. The bare surface used to be what forgetting the
    // directive got you, and the runs it produced were labelled by the model they meant to
    // test while wearing a surface nothing was trained on. Resolved once, at the first run,
    // from config keyed by the provider label in effect; refused if config is silent too.
    private void EnsureHarnessNamed()
    {
        if (_harnessNamed) return;

        var name = _defaultHarness?.Invoke(Provider);
        if (string.IsNullOrWhiteSpace(name))
            throw new NbStartupException(HarnessRegistry.RequiredMessage);
        if (!HarnessRegistry.IsKnown(name))
            throw new NbStartupException($"unknown harness '{name}' in config. Known: {HarnessRegistry.KnownNamesForError()}.");

        Harness = HarnessRegistry.Canonicalize(name);
        _harnessNamed = true;
        ApplyHarness(Harness);
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
            case "oracle_turns":
                // Positive, like every other budget key — Program.cs rejects <= 0 for all
                // of them before the evaluator sees it. (Whether `oracle_turns 0` should
                // become a legal way to detect asks without servicing them is a real
                // question, but it is not this step's; it would need its own exit-reason
                // story rather than a quiet exception to a uniform rule.)
                OracleTurns = bg.Value;
                break;
            default:
                _warnings.Add($"budget key '{bg.Key}' unknown (tokens | tool_calls | wall_ms | oracle_turns) — ignored");
                break;
        }
    }

    private void SwapClient()
    {
        var client = _clientFactory(Provider, Model);
        if (client is null)
        {
            // Hard-fail rather than warn: a program naming a provider is asserting a
            // dependency, and keeping the previous client live answers the question with
            // the wrong thing — silently, at exit 0, with a transcript that cannot be
            // told from a working one. The REPL catches this and stays alive; the
            // program path reports it and exits 1.
            // bugs/Failed_Provider_Directive_Silently_Substitutes.md
            throw new ProviderUnavailableException(
                $"could not build a client for provider '{Provider ?? "(default)"}' model '{Model ?? "(default)"}'. " +
                "The run is aborted rather than answered by the previously selected provider.");
        }
        _conversation.SwitchProvider(client, Provider ?? _conversation.GetCurrentProvider());
    }
}
