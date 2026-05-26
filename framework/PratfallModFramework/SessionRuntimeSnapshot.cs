namespace PratfallModFramework;

// Captures the pre-session runtime mod-enable state before P4 applies any session-scoped
// changes. The future P4.3 SessionEndHooks restore reads from this snapshot to revert
// _modEnabled back to its pre-session shape WITHOUT reading _desiredEnabled live (which
// can drift if the user toggles other mods mid-session) and WITHOUT calling
// WriteLoadedModsToFile.
//
// P4.0 ships this as a SHELL: the type + capture helper only. The actual snapshot capture
// call site, restore call site, and SessionEndHooks integration are P4.3 work.
public sealed class SessionRuntimeSnapshot
{
    // Per-mod runtime enabled state at the moment of capture. Case-insensitive on ModId.
    public Dictionary<string, bool> ModEnabledSnapshot { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Mod IDs the P4 apply path session-toggled. Tracked so P4.3 restore knows which mods
    // need a runtime revert (the rest were already in their snapshot state).
    public HashSet<string> SessionAppliedMods { get; init; }
        = new(StringComparer.OrdinalIgnoreCase);

    public static SessionRuntimeSnapshot CaptureFrom(IReadOnlyDictionary<string, bool> modEnabled)
    {
        var snap = new SessionRuntimeSnapshot();
        foreach (var pair in modEnabled)
            snap.ModEnabledSnapshot[pair.Key] = pair.Value;
        return snap;
    }
}
