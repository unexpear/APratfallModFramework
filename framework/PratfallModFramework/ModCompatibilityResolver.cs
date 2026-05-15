namespace PratfallModFramework;

public static class ModCompatibilityResolver
{
    public static List<ModCompatibilityDecision> Compare(
        IEnumerable<ModManifest> localManifests,
        IEnumerable<ModManifest> remoteManifests)
    {
        var localById = CreateManifestMap(localManifests);
        var remoteById = CreateManifestMap(remoteManifests);
        var decisions = new List<ModCompatibilityDecision>();

        foreach (var modId in localById.Keys.Intersect(remoteById.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var local = localById[modId];
            var remote = remoteById[modId];
            var remoteMode = remote.GetEffectiveNetworkMode();

            if (remoteMode == ModNetworkModes.LocalOnly)
            {
                decisions.Add(ModCompatibilityDecision.Ignored(remote, "Remote mod is marked local-only and will not be synced."));
                continue;
            }

            if (string.Equals(local.Version, remote.Version, StringComparison.OrdinalIgnoreCase))
                continue;

            decisions.Add(ModCompatibilityDecision.RemoteCandidate(
                remote,
                remoteMode,
                localManifest: local,
                conflictingLocalMods: new[] { local.Id },
                additionalRequiredMods: GetAdditionalRequiredMods(remote, localById, remoteById),
                unavailableDependencies: GetUnavailableDependencies(remote, localById, remoteById),
                reason: $"Peer has {remote.Name} {remote.Version}, local is {local.Version}."));
        }

        foreach (var modId in remoteById.Keys.Except(localById.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var remote = remoteById[modId];
            var remoteMode = remote.GetEffectiveNetworkMode();

            if (remoteMode == ModNetworkModes.LocalOnly)
            {
                decisions.Add(ModCompatibilityDecision.Ignored(remote, "Remote mod is marked local-only and will not be synced."));
                continue;
            }

            decisions.Add(ModCompatibilityDecision.RemoteCandidate(
                remote,
                remoteMode,
                localManifest: null,
                conflictingLocalMods: GetConflictingLocalMods(remote, localById.Values),
                additionalRequiredMods: GetAdditionalRequiredMods(remote, localById, remoteById),
                unavailableDependencies: GetUnavailableDependencies(remote, localById, remoteById),
                reason: $"Peer has {remote.Name} {remote.Version} and the local player does not."));
        }

        foreach (var modId in localById.Keys.Except(remoteById.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var local = localById[modId];
            var localMode = local.GetEffectiveNetworkMode();
            if (localMode == ModNetworkModes.LocalOnly)
                continue;

            decisions.Add(ModCompatibilityDecision.PeerMissing(
                local,
                localMode,
                $"Peer is missing local mod {local.Name} {local.Version}."));
        }

        return decisions;
    }

    private static Dictionary<string, ModManifest> CreateManifestMap(IEnumerable<ModManifest> manifests)
    {
        var map = new Dictionary<string, ModManifest>(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            manifest.Normalize();
            if (string.IsNullOrWhiteSpace(manifest.Id) || map.ContainsKey(manifest.Id))
                continue;
            map[manifest.Id] = manifest;
        }
        return map;
    }

    private static List<string> GetConflictingLocalMods(ModManifest remote, IEnumerable<ModManifest> localManifests)
    {
        var conflicts = new List<string>();
        foreach (var local in localManifests)
        {
            if (string.Equals(local.Id, remote.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!remote.DeclaresConflictWith(local.Id) && !local.DeclaresConflictWith(remote.Id))
                continue;

            conflicts.Add(local.Id);
        }

        return ModManifestJson.NormalizeIdentifiers(conflicts);
    }

    private static List<string> GetAdditionalRequiredMods(
        ModManifest remote,
        IReadOnlyDictionary<string, ModManifest> localById,
        IReadOnlyDictionary<string, ModManifest> remoteById)
    {
        return remote.Multiplayer.Requires
            .Where(id => !localById.ContainsKey(id) && remoteById.ContainsKey(id))
            .ToList();
    }

    private static List<string> GetUnavailableDependencies(
        ModManifest remote,
        IReadOnlyDictionary<string, ModManifest> localById,
        IReadOnlyDictionary<string, ModManifest> remoteById)
    {
        return remote.Multiplayer.Requires
            .Where(id => !localById.ContainsKey(id) && !remoteById.ContainsKey(id))
            .ToList();
    }
}

public enum ModCompatibilityDecisionKind
{
    RemoteCandidate,
    PeerMissing,
    Ignored,
}

public sealed class ModCompatibilityDecision
{
    public ModCompatibilityDecisionKind Kind { get; init; }
    public string ModId { get; init; } = "";
    public ModManifest? LocalManifest { get; init; }
    public ModManifest? RemoteManifest { get; init; }
    public string EffectiveMode { get; init; } = ModNetworkModes.Auto;
    public List<string> ConflictingLocalMods { get; init; } = new();
    public List<string> AdditionalRequiredMods { get; init; } = new();
    public List<string> UnavailableDependencies { get; init; } = new();
    public string Reason { get; init; } = "";

    public static ModCompatibilityDecision RemoteCandidate(
        ModManifest remoteManifest,
        string effectiveMode,
        ModManifest? localManifest,
        IEnumerable<string> conflictingLocalMods,
        IEnumerable<string> additionalRequiredMods,
        IEnumerable<string> unavailableDependencies,
        string reason)
    {
        return new ModCompatibilityDecision
        {
            Kind = ModCompatibilityDecisionKind.RemoteCandidate,
            ModId = remoteManifest.Id,
            LocalManifest = localManifest,
            RemoteManifest = remoteManifest,
            EffectiveMode = effectiveMode,
            ConflictingLocalMods = ModManifestJson.NormalizeIdentifiers(conflictingLocalMods),
            AdditionalRequiredMods = ModManifestJson.NormalizeIdentifiers(additionalRequiredMods),
            UnavailableDependencies = ModManifestJson.NormalizeIdentifiers(unavailableDependencies),
            Reason = reason,
        };
    }

    public static ModCompatibilityDecision PeerMissing(ModManifest localManifest, string effectiveMode, string reason)
    {
        return new ModCompatibilityDecision
        {
            Kind = ModCompatibilityDecisionKind.PeerMissing,
            ModId = localManifest.Id,
            LocalManifest = localManifest,
            EffectiveMode = effectiveMode,
            Reason = reason,
        };
    }

    public static ModCompatibilityDecision Ignored(ModManifest manifest, string reason)
    {
        return new ModCompatibilityDecision
        {
            Kind = ModCompatibilityDecisionKind.Ignored,
            ModId = manifest.Id,
            RemoteManifest = manifest,
            EffectiveMode = manifest.GetEffectiveNetworkMode(),
            Reason = reason,
        };
    }
}
