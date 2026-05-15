using System.Text.Json;
using Godot;

namespace PratfallModFramework;

internal sealed class DebugPeerConfig
{
    private static string? _lastLoadFailureFingerprint;

    public const string ConfigPath = "user://modframework-debug-peer.json";

    public bool Enabled { get; set; }
    public string LocalUserId { get; set; } = "debug-host";
    public string PeerUserId { get; set; } = "debug-peer";
    public bool MirrorLocalInstalledManifests { get; set; } = true;
    public List<ModManifest> InstalledManifests { get; set; } = new();
    public List<string> EnabledModIds { get; set; } = new();
    public bool DefaultVoteYes { get; set; } = true;
    public Dictionary<string, bool> VoteResponses { get; set; } = new();

    public static DebugPeerConfig? TryLoad()
    {
        var path = ProjectSettings.GlobalizePath(ConfigPath);
        if (!File.Exists(path))
        {
            _lastLoadFailureFingerprint = null;
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<DebugPeerConfig>(json, ModNetworkJson.Options);
            if (config == null)
                return null;

            config.Normalize();
            _lastLoadFailureFingerprint = null;
            return config.Enabled ? config : null;
        }
        catch (Exception ex)
        {
            var fingerprint = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{ex.Message}";
            if (!string.Equals(_lastLoadFailureFingerprint, fingerprint, StringComparison.Ordinal))
            {
                GD.PrintErr($"[ModFramework] Failed to load debug peer config at {path}: {ex.Message}");
                _lastLoadFailureFingerprint = fingerprint;
            }

            return null;
        }
    }

    public void Normalize()
    {
        LocalUserId = string.IsNullOrWhiteSpace(LocalUserId) ? "debug-host" : LocalUserId.Trim();
        PeerUserId = string.IsNullOrWhiteSpace(PeerUserId) ? "debug-peer" : PeerUserId.Trim();
        if (string.Equals(LocalUserId, PeerUserId, StringComparison.OrdinalIgnoreCase))
            PeerUserId = "debug-peer";

        InstalledManifests ??= new List<ModManifest>();
        EnabledModIds ??= new List<string>();
        VoteResponses ??= new Dictionary<string, bool>();

        foreach (var manifest in InstalledManifests)
            manifest.Normalize();

        EnabledModIds = ModManifestJson.NormalizeIdentifiers(EnabledModIds);
        VoteResponses = VoteResponses
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public ModPeerSnapshot CreatePeerSnapshot(ModLocalState localState)
    {
        localState.Normalize();
        var installedManifests = GetInstalledManifests(localState);
        var installedIds = new HashSet<string>(
            installedManifests.Select(manifest => manifest.Id),
            StringComparer.OrdinalIgnoreCase);

        var snapshot = new ModPeerSnapshot
        {
            UserId = PeerUserId,
            MemberIndex = 1,
            InstalledManifests = installedManifests,
            EnabledModIds = EnabledModIds
                .Where(installedIds.Contains)
                .ToList()
        };
        snapshot.Normalize();
        return snapshot;
    }

    public bool ResolveVote(string modId)
    {
        return VoteResponses.TryGetValue(modId, out var voteYes)
            ? voteYes
            : DefaultVoteYes;
    }

    public void ApplyApprovedResult(ModVoteResult result, ModLocalState localState)
    {
        result.Normalize();
        localState.Normalize();

        if (!result.Passed)
            return;

        var installedById = GetInstalledManifestMap(localState);
        if (!installedById.TryGetValue(result.Manifest.Id, out var installedManifest))
        {
            GD.Print($"[ModFramework] Debug peer approved {result.Manifest.Id}, but the simulated peer does not have it installed");
            return;
        }

        var enabledSet = new HashSet<string>(EnabledModIds, StringComparer.OrdinalIgnoreCase);
        foreach (var enabledId in enabledSet.ToList())
        {
            if (string.Equals(enabledId, installedManifest.Id, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!installedById.TryGetValue(enabledId, out var enabledManifest))
                continue;

            if (installedManifest.DeclaresConflictWith(enabledId) || enabledManifest.DeclaresConflictWith(installedManifest.Id))
                enabledSet.Remove(enabledId);
        }

        enabledSet.Add(installedManifest.Id);
        EnabledModIds = enabledSet.ToList();
    }

    private IReadOnlyDictionary<string, ModManifest> GetInstalledManifestMap(ModLocalState localState)
    {
        return GetInstalledManifests(localState)
            .GroupBy(manifest => manifest.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }

    private List<ModManifest> GetInstalledManifests(ModLocalState localState)
    {
        var source = MirrorLocalInstalledManifests
            ? localState.InstalledManifests
            : InstalledManifests;

        return source
            .Select(CloneManifest)
            .ToList();
    }

    private static ModManifest CloneManifest(ModManifest manifest)
    {
        return ModNetworkJson.CloneManifest(manifest);
    }
}
