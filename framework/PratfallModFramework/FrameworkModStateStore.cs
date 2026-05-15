using System.Text.Json;
using Godot;

namespace PratfallModFramework;

internal static class FrameworkModStateStore
{
    private const string StatePath = "user://modframework-state.json";

    public static HashSet<string> LoadEnabledIds(IReadOnlyCollection<ModManifest> manifests)
    {
        var enabledIds = TryLoadPersistedState(manifests);
        if (enabledIds == null)
            enabledIds = SeedInitialEnabledIds(manifests);

        return enabledIds;
    }

    public static void SaveState(IEnumerable<string> knownModIds, IEnumerable<string> enabledIds)
    {
        var payload = new PersistedState
        {
            KnownModIds = ModManifestJson.NormalizeIdentifiers(knownModIds),
            EnabledModIds = ModManifestJson.NormalizeIdentifiers(enabledIds)
        };

        var path = ProjectSettings.GlobalizePath(StatePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(payload, ModStateJson.Options));
    }

    private static HashSet<string>? TryLoadPersistedState(IReadOnlyCollection<ModManifest> manifests)
    {
        var path = ProjectSettings.GlobalizePath(StatePath);
        if (!File.Exists(path))
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(path), ModStateJson.Options)
                ?? new PersistedState();
            var enabled = new HashSet<string>(
                ModManifestJson.NormalizeIdentifiers(payload.EnabledModIds),
                StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(
                ModManifestJson.NormalizeIdentifiers(payload.KnownModIds),
                StringComparer.OrdinalIgnoreCase);

            foreach (var manifest in manifests.Where(manifest => !manifest.UsesOfficialLoader()))
            {
                if (!known.Contains(manifest.Id))
                    enabled.Add(manifest.Id);
            }

            return enabled;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to load desired mod state: {ex.Message}");
            return null;
        }
    }

    private static HashSet<string> SeedInitialEnabledIds(IReadOnlyCollection<ModManifest> manifests)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            if (!manifest.UsesOfficialLoader())
                enabled.Add(manifest.Id);
        }

        var officialEnabledDirectories = OfficialModBridge.ReadPhysicalEnabledDirectories();
        foreach (var manifest in manifests.Where(manifest => manifest.UsesOfficialLoader()))
        {
            if (officialEnabledDirectories.Contains(manifest.DirectoryName))
                enabled.Add(manifest.Id);
        }

        return enabled;
    }

    private sealed class PersistedState
    {
        public List<string> KnownModIds { get; set; } = new();
        public List<string> EnabledModIds { get; set; } = new();
    }

    private static class ModStateJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
    }
}
