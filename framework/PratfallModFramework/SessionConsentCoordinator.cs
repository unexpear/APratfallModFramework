namespace PratfallModFramework;

// P4.2: drives per-player local consent prompts for the actions emitted by
// SessionApplyPlanner. Strictly bookkeeping + UI dispatch — does NOT call the
// loader, mutate _modEnabled / _desiredEnabled, persist anything, broadcast,
// disconnect, or download. Decisions recorded here are consumed by P4.3 when
// it actually applies the session and by SessionEndHooks when it restores.
//
// Why a separate class (instead of inlining into ModManager): keeps the queue,
// dedup keys, in-flight gate, and finalize callback testable without spinning
// up a real ModManager (which needs a SceneTree, NetworkLayer, etc.). The
// ShowPrompt callback is wired by ModManager to the real Godot UI; tests
// either set PromptOverride for a synchronous answer or set ShowPrompt to a
// test stub.
internal sealed class SessionConsentCoordinator
{
    // Test seam: synchronous override invoked instead of ShowPrompt. When set,
    // the coordinator never calls ShowPrompt and never blocks on a callback.
    // Matches the user-spec test-seam shape `Func<SessionApplyAction, SessionConsentDecision>?`.
    public System.Func<SessionApplyAction, SessionConsentDecision>? PromptOverride { get; set; }

    // Production UI hook. Receives the action and a resolve callback; the host
    // (ModManager) shows the dedicated Godot prompt and invokes the callback
    // exactly once when the user clicks a button. Null in headless contexts.
    public System.Action<SessionApplyAction, System.Action<SessionConsentDecision>>? ShowPrompt { get; set; }

    // Logger sink. Wired to GD.Print by ModManager so consent decisions show
    // up alongside the rest of the [ModFramework] log; null in tests that
    // don't want noise.
    public System.Action<string>? Log { get; set; }

    private readonly Dictionary<string, SessionConsentDecision> _decisions =
        new(StringComparer.Ordinal);
    private readonly Queue<(SessionApplyAction Action, string PlanSignature)> _pending = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private bool _inFlight;
    private bool _leaveRequired;

    public IReadOnlyDictionary<string, SessionConsentDecision> Decisions => _decisions;
    public bool LeaveRequired => _leaveRequired;
    public int PendingCount => _pending.Count;
    public bool InFlight => _inFlight;

    // Stable composite key for an (action, plan) pair. Used for dedup so the
    // same action under the same plan signature can only be prompted/recorded
    // once. Different plan signature ⇒ different key ⇒ fresh prompt (the
    // host's plan dedup at OnSessionPlanResolvedReceived already guards
    // against identical-signature re-deliveries).
    public static string BuildKey(SessionApplyAction action, string planSignature) =>
        planSignature + "|" + ((int)action.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (action.ModId ?? "");

    public void EnqueueActions(IReadOnlyList<SessionApplyAction> actions, string planSignature)
    {
        if (actions == null) return;
        foreach (var action in actions)
        {
            // NoChange: nothing to prompt about. The planner emits these for
            // already-in-the-right-state mods; no user decision needed.
            if (action.Kind == SessionApplyActionKind.NoChange) continue;

            var key = BuildKey(action, planSignature);
            // Already decided in this session: keep the existing decision.
            if (_decisions.ContainsKey(key)) continue;
            // Already queued (e.g. duplicate-action arrival during a prompt): skip.
            if (!_queuedKeys.Add(key)) continue;

            _pending.Enqueue((action, planSignature));
        }
        TryDriveNext();
    }

    private void TryDriveNext()
    {
        if (_inFlight) return;
        if (_pending.Count == 0) return;

        var (action, planSignature) = _pending.Dequeue();
        _inFlight = true;

        // Priority: PromptOverride (tests) > ShowPrompt (real UI) > auto-default
        // (headless / no UI wired). Auto-default is the safe choice: a user who
        // can't see a prompt can't consent, so the only correct outcome is
        // LeaveRequired (or Acknowledged for the informational CannotContinue).
        if (PromptOverride != null)
        {
            var decision = PromptOverride(action);
            Finalize(action, planSignature, decision);
            return;
        }

        if (ShowPrompt != null)
        {
            ShowPrompt(action, decision => Finalize(action, planSignature, decision));
            return;
        }

        Finalize(action, planSignature, AutoDefault(action));
    }

    private void Finalize(SessionApplyAction action, string planSignature, SessionConsentDecision decision)
    {
        var key = BuildKey(action, planSignature);
        _decisions[key] = decision;
        _queuedKeys.Remove(key);
        if (decision == SessionConsentDecision.LeaveRequired)
            _leaveRequired = true;
        Log?.Invoke("[ModFramework] Session consent recorded: " + action.Kind + " " + (action.ModId ?? "") + " -> " + decision);
        _inFlight = false;
        TryDriveNext();
    }

    // Headless / no-UI fallback. CannotContinue is informational, so we
    // acknowledge it; everything else needs a leave (because we cannot apply
    // safely without the user's explicit consent).
    private static SessionConsentDecision AutoDefault(SessionApplyAction action) =>
        action.Kind == SessionApplyActionKind.CannotContinue
            ? SessionConsentDecision.Acknowledged
            : SessionConsentDecision.LeaveRequired;

    // Returns the recorded decision for an action under a given plan signature,
    // or None if no decision has been recorded yet.
    public SessionConsentDecision GetDecision(SessionApplyAction action, string planSignature)
    {
        var key = BuildKey(action, planSignature);
        return _decisions.TryGetValue(key, out var d) ? d : SessionConsentDecision.None;
    }
}
