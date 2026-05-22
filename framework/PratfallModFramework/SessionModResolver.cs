namespace PratfallModFramework;

// Pure, session-scoped mod-matching resolver (V1 / phase P1). Given the host's desired
// enabled mods and every player's installed inventory, it computes a temporary
// EffectiveSessionModSet plus the compatibility decisions a lobby would resolve before a
// session starts. It NEVER mutates the host's saved enabled state. No network, no UI, no
// votes executed here — Resolve produces the PLAN; vote application is a later phase.
// See SESSION_MOD_RESOLVER_PLAN.md.
//
// Core rule (from the plan): session safety wins unless every player explicitly accepts
// the risk. A clean mod auto-enables. A mod that can't satisfy everyone (a player is
// missing it, a version mismatch, a missing dependency, or a declared conflict with an
// already-effective mod) is disabled for the session by default; enabling it anyway is an
// UNSAFE override that requires a unanimous vote.
//
// P1 scope note: declared-conflict pairs default to keeping the earlier-listed mod and
// disabling the later one, offering a unanimous "keep both" override. The safe-majority
// "which-to-keep" refinement is deferred to a later phase.
public static class SessionModResolver
{
    public static SessionResolutionPlan Resolve(ModLocalState hostState, IReadOnlyList<ModPeerSnapshot> peers)
    {
        hostState.Normalize();
        var peerList = peers ?? (IReadOnlyList<ModPeerSnapshot>)Array.Empty<ModPeerSnapshot>();
        foreach (var peer in peerList) peer.Normalize();

        var plan = new SessionResolutionPlan();
        var hostInstalled = BuildManifestMap(hostState.InstalledManifests);

        // Desired = host's enabled mods that are actually installed locally, in declared order.
        var desired = new List<ModManifest>();
        var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in hostState.EnabledModIds)
            if (hostInstalled.TryGetValue(id, out var manifest) && desiredIds.Add(id))
                desired.Add(manifest);

        // Declared conflicts among desired mods: keep the earlier-listed, disable the later.
        var conflictWarningByMod = new Dictionary<string, SessionCompatibilityWarning>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < desired.Count; i++)
            for (var j = i + 1; j < desired.Count; j++)
            {
                var keep = desired[i];
                var drop = desired[j];
                if (keep.DeclaresConflictWith(drop.Id) || drop.DeclaresConflictWith(keep.Id))
                    conflictWarningByMod.TryAdd(drop.Id, Warn(
                        SessionWarningKind.DeclaredConflict, drop.Id, null,
                        $"{drop.Id} declares a conflict with already-effective {keep.Id}"));
            }

        foreach (var mod in desired)
        {
            var modWarnings = new List<SessionCompatibilityWarning>();
            if (conflictWarningByMod.TryGetValue(mod.Id, out var conflictWarning))
                modWarnings.Add(conflictWarning);

            // Per-player: missing entirely, or installed with a different version.
            foreach (var peer in peerList)
            {
                var peerInstalled = peer.GetInstalledManifestMap();
                if (!peerInstalled.TryGetValue(mod.Id, out var peerMod))
                    modWarnings.Add(Warn(SessionWarningKind.MissingForPlayer, mod.Id, peer.UserId,
                        $"{peer.UserId} does not have {mod.Id} installed"));
                else if (!string.Equals(peerMod.Version, mod.Version, StringComparison.OrdinalIgnoreCase))
                    modWarnings.Add(Warn(SessionWarningKind.VersionMismatch, mod.Id, peer.UserId,
                        $"host has {mod.Id} {mod.Version}, {peer.UserId} has {peerMod.Version}"));
            }

            // Required dependency not in the host's desired set.
            foreach (var dependencyId in mod.Multiplayer.Requires)
                if (!desiredIds.Contains(dependencyId))
                    modWarnings.Add(Warn(SessionWarningKind.MissingDependency, mod.Id, null,
                        $"{mod.Id} requires {dependencyId}, which is not in the host's enabled set"));

            plan.Warnings.AddRange(modWarnings);

            if (modWarnings.Count == 0)
            {
                // Clean: every player has a compatible copy, no conflict, deps satisfied.
                plan.EffectiveSessionModSet.Add(mod.Id);
                plan.Decisions.Add(new SessionDecision
                {
                    ModId = mod.Id,
                    ProposedState = SessionProposedState.Enable,
                    Safety = SessionDecisionSafety.Safe,
                    VoteRule = SessionVoteRule.None,
                    Reason = "all players have a compatible copy; auto-enabled",
                });
                continue;
            }

            // Contentious: safe default is to disable for the session. Enabling anyway is an
            // unsafe override that needs a unanimous vote.
            plan.DisabledForSession.Add(mod.Id);
            var isConflict = conflictWarningByMod.ContainsKey(mod.Id);
            var decision = new SessionDecision
            {
                ModId = mod.Id,
                ProposedState = SessionProposedState.Enable,
                Safety = SessionDecisionSafety.Unsafe,
                VoteRule = SessionVoteRule.Unanimous,
                Warnings = modWarnings,
                Reason = isConflict
                    ? "declared conflict — disabled for the session; a unanimous vote is required to keep both"
                    : "compatibility warning — disabled for the session; a unanimous vote is required to enable anyway",
            };
            plan.Decisions.Add(decision);
            plan.PendingVotes.Add(decision);
        }

        plan.Resolved = false; // P1 produces the plan; votes are applied in a later phase.
        return plan;
    }

    private static SessionCompatibilityWarning Warn(SessionWarningKind kind, string modId, string? player, string detail)
    {
        var warning = new SessionCompatibilityWarning { Kind = kind, ModId = modId, Detail = detail };
        if (!string.IsNullOrWhiteSpace(player))
            warning.AffectedPlayers.Add(player);
        return warning;
    }

    private static Dictionary<string, ModManifest> BuildManifestMap(IEnumerable<ModManifest> manifests)
    {
        var map = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            manifest.Normalize();
            if (!string.IsNullOrWhiteSpace(manifest.Id))
                map.TryAdd(manifest.Id, manifest);
        }
        return map;
    }
}

public enum SessionWarningKind
{
    MissingForPlayer = 0,
    MissingDependency = 1,
    DeclaredConflict = 2,
    VersionMismatch = 3,
}

public enum SessionDecisionSafety
{
    Safe = 0,
    Unsafe = 1,
}

public enum SessionProposedState
{
    Enable = 0,
    Disable = 1,
}

public enum SessionVoteRule
{
    None = 0,
    Majority = 1,
    Unanimous = 2,
}

public sealed class SessionCompatibilityWarning
{
    public SessionWarningKind Kind { get; init; }
    public string ModId { get; init; } = "";
    public List<string> AffectedPlayers { get; init; } = new();
    public string Detail { get; init; } = "";

    public override string ToString() =>
        $"{Kind}: {ModId}{(AffectedPlayers.Count > 0 ? $" [{string.Join(", ", AffectedPlayers)}]" : "")} — {Detail}";
}

public sealed class SessionDecision
{
    public string ModId { get; init; } = "";
    public SessionProposedState ProposedState { get; init; }
    public SessionDecisionSafety Safety { get; init; }
    public SessionVoteRule VoteRule { get; init; }
    public List<SessionCompatibilityWarning> Warnings { get; init; } = new();
    public string Reason { get; init; } = "";

    public override string ToString() =>
        $"{ProposedState} {ModId} ({Safety}, vote={VoteRule}): {Reason}";
}

public sealed class SessionResolutionPlan
{
    public List<SessionCompatibilityWarning> Warnings { get; } = new();
    public List<SessionDecision> Decisions { get; } = new();
    public List<SessionDecision> PendingVotes { get; } = new();
    public HashSet<string> EffectiveSessionModSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledForSession { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ApprovedOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RejectedOverrides { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Resolved { get; set; }

    public override string ToString() =>
        $"plan: effective={EffectiveSessionModSet.Count} disabled={DisabledForSession.Count} " +
        $"warnings={Warnings.Count} pendingVotes={PendingVotes.Count} resolved={Resolved}";
}
