namespace PratfallModFramework;

// Pure planner (P4.0). Given a RESOLVED SessionResolutionPlan + local installed mods +
// the saved enabled-mod preference + the current runtime-loaded state, produce the list
// of SessionApplyActions the consent flow should consider for THIS player.
//
// Strict scope: this planner ONLY produces an action list. It does NOT mutate inputs,
// does NOT touch the loader, does NOT prompt the user, does NOT broadcast or persist
// anything. Runtime apply behavior (consent prompts, loader calls, snapshot restore) is
// P4.2/P4.3 work that consumes this planner's output. See SESSION_MOD_RESOLVER_PLAN.md
// "Hard policy" section.
//
// Live-eligibility (Hard policy, Option A — strict, + P4.3 PCK rule):
//   A mod is eligible for a live session enable/disable ONLY if BOTH hold:
//     1. Multiplayer.Mode == "local_only"  (else hot-changing it could shift
//        NetworkPrefabsConfig indices and desync replication), AND
//     2. it does NOT mount a resource pack (RequiresPckMount == false). Godot 4 cannot
//        unmount a PCK once LoadResourcePack mounts it (see ModManager.MountModPckIfAny),
//        so a "temporary" enable/disable of a PCK-backed mod is not cleanly reversible —
//        session restore would leave resources stranded on res://.
//   Anything failing either test yields UnsafeHotEnableRequiresRejoin (player must
//   leave + rejoin, or restart, to apply the change safely).
public static class SessionApplyPlanner
{
    public static List<SessionApplyAction> Plan(
        SessionResolutionPlan? plan,
        IReadOnlyList<ModManifest> installedMods,
        IReadOnlyDictionary<string, bool> desiredEnabled,
        IReadOnlyDictionary<string, bool> runtimeEnabled)
    {
        var actions = new List<SessionApplyAction>();

        if (plan == null || !plan.Resolved)
        {
            actions.Add(new SessionApplyAction
            {
                ModId = "",
                Kind = SessionApplyActionKind.CannotContinue,
                Reason = plan == null
                    ? "no session plan available"
                    : "session plan is not resolved yet (votes still pending)",
            });
            return actions;
        }

        var installedById = BuildInstalledMap(installedMods);

        // Enables: every mod the resolved plan wants effective this session.
        foreach (var modId in plan.EffectiveSessionModSet.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            if (!installedById.TryGetValue(modId, out var manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.MissingRequiredMod,
                    Reason = $"session plan requires '{modId}' but it is not installed locally",
                });
                continue;
            }

            var alreadyOn = runtimeEnabled.TryGetValue(modId, out var on) && on;
            if (alreadyOn)
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.NoChange,
                    Reason = "already loaded; no change needed",
                });
                continue;
            }

            if (!IsLocalOnly(manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.UnsafeHotEnableRequiresRejoin,
                    Reason = $"mod is not local_only (Multiplayer.Mode='{NormalizeMode(manifest)}'); hot-enable could shift NetworkPrefabsConfig indices — leave + rejoin required to apply safely",
                });
            }
            else if (RequiresPckMount(manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.UnsafeHotEnableRequiresRejoin,
                    Reason = "mod mounts a resource pack (Godot 4 cannot unmount PCKs) so a live enable is not cleanly reversible — leave + rejoin (or restart) required to apply safely",
                });
            }
            else
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.EnableInstalledForSession,
                    Reason = "installed but disabled; local_only mod with no resource pack can be hot-enabled for this session (consent required at apply time)",
                });
            }
        }

        // Disables: every mod the resolved plan wants OFF this session.
        foreach (var modId in plan.DisabledForSession.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            if (!installedById.TryGetValue(modId, out var manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.NoChange,
                    Reason = "not installed locally; nothing to disable",
                });
                continue;
            }

            var currentlyOn = runtimeEnabled.TryGetValue(modId, out var on) && on;
            if (!currentlyOn)
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.NoChange,
                    Reason = "already not loaded; no change needed",
                });
                continue;
            }

            if (!IsLocalOnly(manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.UnsafeHotEnableRequiresRejoin,
                    Reason = $"mod is not local_only (Multiplayer.Mode='{NormalizeMode(manifest)}'); hot-disable could shift NetworkPrefabsConfig indices — leave + rejoin required to apply safely",
                });
            }
            else if (RequiresPckMount(manifest))
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.UnsafeHotEnableRequiresRejoin,
                    Reason = "mod mounts a resource pack (Godot 4 cannot unmount PCKs) so a live disable is not cleanly reversible — leave + rejoin (or restart) required to apply safely",
                });
            }
            else
            {
                actions.Add(new SessionApplyAction
                {
                    ModId = modId,
                    Kind = SessionApplyActionKind.DisableForSession,
                    Reason = "currently loaded; local_only mod with no resource pack can be hot-disabled for this session (consent required at apply time)",
                });
            }
        }

        // Planner intentionally does NOT mutate `desiredEnabled` or `runtimeEnabled`.
        // The IReadOnly* signature is the contract; tests assert input dictionaries are
        // unchanged after the call (test #7 in RunSessionApplyPlannerTests).
        return actions;
    }

    private static bool IsLocalOnly(ModManifest manifest)
    {
        var mode = manifest.Multiplayer?.Mode;
        return string.Equals(mode, ModNetworkModes.LocalOnly, StringComparison.OrdinalIgnoreCase);
    }

    // P4.3: true when the mod mounts an asset package that Godot 4 cannot unmount at
    // runtime, making a live session enable/disable irreversible. Conservative union of
    // both package signals: PckFile (the framework PCK-mount input read by
    // ModManager.MountModPckIfAny) and PackageName (the official-loader package, also
    // pck-backed). Either present ⇒ not live-eligible. Shared by the planner (to classify
    // as UnsafeHotEnableRequiresRejoin) and by P4.3 apply as defense-in-depth.
    internal static bool RequiresPckMount(ModManifest manifest) =>
        manifest != null &&
        (!string.IsNullOrWhiteSpace(manifest.PckFile) ||
         !string.IsNullOrWhiteSpace(manifest.PackageName));

    private static string NormalizeMode(ModManifest manifest) =>
        ModNetworkModes.Normalize(manifest.Multiplayer?.Mode);

    private static Dictionary<string, ModManifest> BuildInstalledMap(IEnumerable<ModManifest> manifests)
    {
        var map = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifests)
        {
            if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
            map.TryAdd(m.Id, m);
        }
        return map;
    }
}

public enum SessionApplyActionKind
{
    NoChange = 0,
    EnableInstalledForSession = 1,
    DisableForSession = 2,
    MissingRequiredMod = 3,
    UnsafeHotEnableRequiresRejoin = 4,
    LeaveRequired = 5,
    CannotContinue = 6,
}

public sealed class SessionApplyAction
{
    public string ModId { get; init; } = "";
    public SessionApplyActionKind Kind { get; init; }
    public string Reason { get; init; } = "";

    public override string ToString() =>
        string.IsNullOrEmpty(ModId) ? $"{Kind}: {Reason}" : $"{Kind} {ModId}: {Reason}";
}
