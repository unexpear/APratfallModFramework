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
    // P4.2 hardening: monotonic token uniquely identifying the prompt currently in
    // flight. Bumped on every new prompt AND on Reset, so a stale Finalize callback
    // carrying its old token always fails the guard — even when the new in-flight
    // action has the same composite key (same plan signature + kind + mod id) as the
    // pre-Reset one, which happens in quick disconnect/reconnect to the same lobby.
    // A content-based key check would falsely match in that case and let the stale
    // decision "win" the race; the token check correctly rejects it.
    private long _inFlightToken;
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
        var token = ++_inFlightToken;

        // Priority: PromptOverride (tests) > ShowPrompt (real UI) > auto-default
        // (headless / no UI wired). Auto-default is the safe choice: a user who
        // can't see a prompt can't consent, so the only correct outcome is
        // LeaveRequired (or Acknowledged for the informational CannotContinue).
        if (PromptOverride != null)
        {
            var decision = PromptOverride(action);
            Finalize(action, planSignature, decision, token);
            return;
        }

        if (ShowPrompt != null)
        {
            ShowPrompt(action, decision => Finalize(action, planSignature, decision, token));
            return;
        }

        Finalize(action, planSignature, AutoDefault(action), token);
    }

    private void Finalize(SessionApplyAction action, string planSignature, SessionConsentDecision decision, long token)
    {
        // P4.2 hardening: reject stale callbacks via monotonic token. If Reset() ran
        // between prompt-show and the user's click, _inFlightToken was bumped — the
        // captured token from the orphaned prompt is now older than _inFlightToken.
        // If a new action started being prompted (even with the same composite key —
        // realistic in quick disconnect/reconnect to the same lobby), the new prompt
        // got its own fresh token, so the stale callback still fails the guard. The
        // token is also bumped on a successful Finalize → TryDriveNext, so an over-
        // eager duplicate Pressed signal that double-fires from the SAME prompt is
        // also caught (its captured token != the new current token).
        if (token != _inFlightToken) return;

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

    // P4.2 hardening: clear all per-connection state. Called by ModManager on transport
    // reset so consent decisions made before a disconnect can't silently suppress fresh
    // prompts after reconnect. Intentionally does NOT clear PromptOverride / ShowPrompt /
    // Log — those are wiring bindings owned by the host, not per-session state.
    public void Reset()
    {
        _decisions.Clear();
        _pending.Clear();
        _queuedKeys.Clear();
        _inFlight = false;
        // Bump the in-flight token so any pre-Reset prompt's captured token is now
        // stale: a delayed Godot signal arriving after Reset will fail the guard in
        // Finalize and be dropped, even if a new prompt arrived for the same action
        // post-Reset (its token would be even newer than this one).
        _inFlightToken++;
        _leaveRequired = false;
    }
}
