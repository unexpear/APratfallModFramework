namespace PratfallModFramework;

// P4.2: the local-player consent outcome recorded for each SessionApplyAction the
// planner emits. Pure bookkeeping — actually applying any of these (loader calls,
// disconnect + rejoin, snapshot restore) is P4.3 work that consumes these records.
public enum SessionConsentDecision
{
    // No decision recorded yet (default for a freshly-built coordinator).
    None = 0,

    // User approved enabling an installed-but-disabled local_only mod for this session.
    // P4.3 will perform the actual hot-enable when it consumes this.
    ApprovedEnable = 1,

    // User approved disabling an installed-and-enabled local_only mod for this session.
    // P4.3 will perform the actual hot-disable when it consumes this.
    ApprovedDisable = 2,

    // User declined an enable/disable, OR the planner reported an action the user
    // cannot proceed with (missing required mod, non-local_only hot-enable). The
    // session-apply state should treat this as "must leave to continue safely."
    // P4.2 does NOT call LeaveLobby — that's P4.3's responsibility.
    LeaveRequired = 3,

    // User acknowledged an informational message (CannotContinue). No apply action.
    Acknowledged = 4,
}
