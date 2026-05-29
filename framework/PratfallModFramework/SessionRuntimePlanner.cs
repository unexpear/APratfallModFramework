namespace PratfallModFramework;

// P4.3: pure decision layer for temporary session-scoped runtime changes. Given the
// planner's actions + the recorded local consent decisions (+ manifests for the
// defense-in-depth eligibility re-check), decide WHICH mods to runtime-enable/disable —
// and, separately, how to revert them at session end. Loader-free and side-effect-free
// so it is unit-testable; ModManager executes the returned ops via EnableMod/DisableMod.
//
// Strict scope: produces op lists only. Never calls the loader, never mutates _modEnabled
// / _desiredEnabled, never persists, never leaves the lobby.
public static class SessionRuntimePlanner
{
    public enum RuntimeOp
    {
        Enable = 0,
        Disable = 1,
    }

    public sealed class RuntimeApplyAction
    {
        public string ModId { get; init; } = "";
        public RuntimeOp Op { get; init; }
        public override string ToString() => $"{Op} {ModId}";
    }

    // Translate consent-decided planner actions into the concrete runtime ops to perform.
    // Only acts on ApprovedEnable -> Enable and ApprovedDisable -> Disable, AND only when
    // the mod still passes live-eligibility (local_only && !PCK) as defense-in-depth. Every
    // other decision (LeaveRequired / Acknowledged / None) and every non-live-eligible mod
    // is skipped here — LeaveRequired is handled by the caller's leave path, not by apply.
    //
    // getDecision: maps a planner action to its recorded SessionConsentDecision (None if
    // the user has not decided). installedById: id -> manifest for the eligibility re-check.
    public static List<RuntimeApplyAction> ComputeApplyActions(
        IReadOnlyList<SessionApplyAction> plannerActions,
        Func<SessionApplyAction, SessionConsentDecision> getDecision,
        IReadOnlyDictionary<string, ModManifest> installedById)
    {
        var ops = new List<RuntimeApplyAction>();
        if (plannerActions == null || getDecision == null) return ops;

        foreach (var action in plannerActions)
        {
            var decision = getDecision(action);

            if (decision == SessionConsentDecision.ApprovedEnable &&
                action.Kind == SessionApplyActionKind.EnableInstalledForSession &&
                IsLiveEligible(action.ModId, installedById))
            {
                ops.Add(new RuntimeApplyAction { ModId = action.ModId, Op = RuntimeOp.Enable });
            }
            else if (decision == SessionConsentDecision.ApprovedDisable &&
                     action.Kind == SessionApplyActionKind.DisableForSession &&
                     IsLiveEligible(action.ModId, installedById))
            {
                ops.Add(new RuntimeApplyAction { ModId = action.ModId, Op = RuntimeOp.Disable });
            }
            // ApprovedEnable/ApprovedDisable whose Kind is NOT the matching apply kind, or
            // which fail the eligibility re-check, are intentionally dropped: the planner
            // should never have paired them, so dropping is the safe (no-op) outcome.
        }

        return ops;
    }

    // Revert only the mods the session-apply path actually toggled, back to their captured
    // pre-session runtime state. A mod already matching its snapshot value yields no op
    // (covers the double-restore / idempotent case once SessionAppliedMods is processed).
    public static List<RuntimeApplyAction> ComputeRestoreActions(
        SessionRuntimeSnapshot? snapshot,
        IReadOnlyDictionary<string, bool> currentEnabled)
    {
        var ops = new List<RuntimeApplyAction>();
        if (snapshot == null || currentEnabled == null) return ops;

        foreach (var modId in snapshot.SessionAppliedMods)
        {
            // Original state at capture (absent ⇒ treat as "was off").
            snapshot.TryGetOriginal(modId, out var wasEnabled);
            var isOn = currentEnabled.TryGetValue(modId, out var cur) && cur;

            if (isOn == wasEnabled) continue; // already in the pre-session shape

            ops.Add(new RuntimeApplyAction
            {
                ModId = modId,
                Op = wasEnabled ? RuntimeOp.Enable : RuntimeOp.Disable,
            });
        }

        return ops;
    }

    // Defense-in-depth: re-assert Option A + the PCK rule at apply time, independent of
    // what the planner classified. A mod missing from the installed map is not eligible.
    private static bool IsLiveEligible(string modId, IReadOnlyDictionary<string, ModManifest> installedById)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        if (installedById == null || !installedById.TryGetValue(modId, out var manifest) || manifest == null)
            return false;
        var isLocalOnly = string.Equals(manifest.Multiplayer?.Mode, ModNetworkModes.LocalOnly, StringComparison.OrdinalIgnoreCase);
        return isLocalOnly && !SessionApplyPlanner.RequiresPckMount(manifest);
    }
}
