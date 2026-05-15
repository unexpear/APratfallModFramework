using System.Text.Json;
using Godot;

namespace PratfallModFramework;

internal static class FrameworkModStateStore
{
    private const string StatePath = "user://modframework-state.json";

    public sealed class LoadedState
    {
        public HashSet<string> EnabledIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // SHA-256 hex fingerprints of mod-states the user has explicitly approved
        // (toggled on, inspected, or accepted via Download). The fingerprint covers
        // every executable file in the mod (DLL + PCK if any), so any byte change in
        // either re-locks the gate. Hash-based — a mod update forces a re-check.
        public HashSet<string> CheckedModFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static LoadedState LoadState(IReadOnlyCollection<ModManifest> manifests)
    {
        var loaded = TryLoadPersistedState();
        if (loaded != null)
            return loaded;

        // First-run / no prior state: seed enabled set from physical official-loader
        // state only. Framework-loaded mods stay disabled until the user explicitly
        // checks or toggles them on.
        return new LoadedState { EnabledIds = SeedInitialEnabledIds(manifests) };
    }

    public static void SaveState(IEnumerable<string> knownModIds, IEnumerable<string> enabledIds, IEnumerable<string> checkedModFingerprints)
    {
        var payload = new PersistedState
        {
            KnownModIds = ModManifestJson.NormalizeIdentifiers(knownModIds),
            EnabledModIds = ModManifestJson.NormalizeIdentifiers(enabledIds),
            CheckedModFingerprints = checkedModFingerprints
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Select(h => h.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        var path = ProjectSettings.GlobalizePath(StatePath);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, JsonSerializer.Serialize(payload, ModStateJson.Options));
    }

    private static LoadedState? TryLoadPersistedState()
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
            var checkedHashes = new HashSet<string>(
                (payload.CheckedModFingerprints ?? new List<string>())
                    .Where(h => !string.IsNullOrWhiteSpace(h))
                    .Select(h => h.Trim().ToUpperInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return new LoadedState { EnabledIds = enabled, CheckedModFingerprints = checkedHashes };
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
        public List<string> CheckedModFingerprints { get; set; } = new();
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
