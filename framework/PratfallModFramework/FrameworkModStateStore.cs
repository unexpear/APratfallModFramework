using System.Text.Json;
using Godot;

namespace PratfallModFramework;

internal static class FrameworkModStateStore
{
    // Default path (single-install Steam launch). Profile-managed launches
    // (--qh-mod-directory present) divert to <profile>/modframework-state.json
    // via FrameworkProfile.ResolveStateFilePath so each r2modman / Thunderstore
    // profile has independent framework state. The DefaultStatePath constant is
    // kept for the legacy ReadPersistedState fallback only.
    private const string DefaultStatePath = "user://modframework-state.json";

    public sealed class LoadedState
    {
        public HashSet<string> EnabledIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // SHA-256 hex fingerprints of mod-states the user has explicitly approved
        // (toggled on, inspected, or accepted via Download). The fingerprint covers
        // every executable file in the mod (DLL + PCK if any), so any byte change in
        // either re-locks the gate. Hash-based — a mod update forces a re-check.
        public HashSet<string> CheckedModFingerprints { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        // Mods we knew about last session, by ID. Compared against the current scan
        // to detect "this mod was here last launch, isn't now" — surfaces as a
        // dropped-mods notice in the Mods dialog. Populated from the persisted
        // file; never empty after a non-first-run launch.
        public HashSet<string> KnownModIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static LoadedState LoadState(IReadOnlyCollection<ModManifest> manifests)
    {
        return TryLoadPersistedState() ?? new LoadedState();
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

        var path = FrameworkProfile.ResolveStateFilePath();
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(payload, ModStateJson.Options));
        }
        catch (Exception ex)
        {
            // Most likely cause: profile folder deleted mid-session (r2modman
            // profile-switch while game open) or write permission lost. In-memory
            // state still tracks the user's intent; we just won't persist this
            // change. Log and continue so the user's mod toggle doesn't surface
            // as an unhandled crash.
            GD.PrintErr($"[ModFramework] FrameworkModStateStore: failed to persist state to {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static LoadedState? TryLoadPersistedState()
    {
        var path = ResolvePathOrNull();
        if (path == null) return null;

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
            var known = new HashSet<string>(
                ModManifestJson.NormalizeIdentifiers(payload.KnownModIds ?? new List<string>()),
                StringComparer.OrdinalIgnoreCase);

            return new LoadedState
            {
                EnabledIds = enabled,
                CheckedModFingerprints = checkedHashes,
                KnownModIds = known,
            };
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to load desired mod state: {ex.Message}");
            return null;
        }
    }

    // Resolves which state-file path to read on this launch. Returns null if no
    // state file exists at all (first-ever launch, or first launch under a fresh
    // r2modman profile with no default-location fallback to migrate from). The
    // migration fallback only kicks in for profile-managed launches — single-
    // install users always go straight to the user:// state.
    private static string? ResolvePathOrNull()
    {
        var primary = FrameworkProfile.ResolveStateFilePath();
        if (File.Exists(primary)) return primary;

        if (!FrameworkProfile.IsActive) return null;

        var fallback = ProjectSettings.GlobalizePath(DefaultStatePath);
        if (!File.Exists(fallback)) return null;

        GD.Print($"[ModFramework] FrameworkModStateStore: profile state file missing; one-time read from default location for migration ({fallback})");
        return fallback;
    }

    private sealed class PersistedState
    {
        public List<string> KnownModIds { get; set; } = new();
        public List<string> EnabledModIds { get; set; } = new();
        public List<string> CheckedModFingerprints { get; set; } = new();
    }

    private static class ModStateJson
    {
        // Cloned from JsonSerializerOptions.Default so the TypeInfoResolver is
        // populated. Matches the same pattern ModConfig.WriteFile uses — Pratfall's
        // runtime config can disable reflection-based JSON serialization, and a
        // fresh `new JsonSerializerOptions { ... }` would then throw "must specify
        // TypeInfoResolver" on first use. Cloning Default sidesteps that whether
        // the runtime has reflection on or off.
        public static readonly JsonSerializerOptions Options = new(JsonSerializerOptions.Default)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
    }
}
