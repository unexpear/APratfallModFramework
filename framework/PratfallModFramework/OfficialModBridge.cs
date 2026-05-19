using HarmonyLib;
using Godot;
using System.Text.Json;

namespace PratfallModFramework;

// "Turn off" patch for Pratfall's native ModManager. Tim shipped Workshop +
// modding fixes on 2026-05-18 with a dramatically expanded ModManager that owns
// discovery, loading, and Workshop integration. He explicitly invited custom
// mod loaders. We accept the invitation: our framework becomes the sole mod
// loader, and we neuter the native one cleanly rather than coexisting with it.
//
// History: this file used to be a "bridge" — we coexisted with the native
// loader, intercepting its startup file reads while bubbling EnableMod /
// DisableMod / IsModEnabled calls back to keep state in sync. The bridge relied
// on `ModManager.GetModManifest(string)` which Tim's update renamed to
// `GetModManifestFromDirectory` AND privatized. Rather than reach into private
// internals via reflection, we step out of the way entirely.
//
// What we patch:
//   - ModManager.LoadAllModManifests : skip — we load mods ourselves
//   - ModManager.ReadLoadedModsFromFile : return empty list — defensive in case
//     anything else calls it post-Setup
//   - ModManager.WriteLoadedModsToFile : no-op — same reason
//
// What native ModManager still does (and we let happen):
//   - CreateModDirectory()  — harmless, creates mods/ folder
//   - Steam.SetupWorkshopCallbacks(...) — registers Workshop install callbacks
//     in Steam's runtime. We discover Workshop mods ourselves by scanning the
//     workshop content folder (see ManifestManager.ScanWorkshopMods).
//
// Names retained for back-compat with framework call sites; the methods that
// used to bridge to native ModManager are now no-ops returning success.
internal static class OfficialModBridge
{
    private static bool _installed;
    private static bool _hasLoggedReadInterception;

    public static void Install()
    {
        if (_installed)
            return;

        var harmony = new Harmony("PratfallModFramework.OfficialModBridge");

        var loadAll = AccessTools.Method(typeof(global::ModManager), "LoadAllModManifests");
        if (loadAll != null)
        {
            harmony.Patch(loadAll, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(LoadAllModManifestsPrefix)));
        }
        else
        {
            GD.PrintErr("[ModFramework] OfficialModBridge: LoadAllModManifests not found — Pratfall version may have changed signatures");
        }

        var read = AccessTools.Method(typeof(global::ModManager), "ReadLoadedModsFromFile");
        if (read != null)
            harmony.Patch(read, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(ReadLoadedModsFromFilePrefix)));

        var write = AccessTools.Method(typeof(global::ModManager), "WriteLoadedModsToFile");
        if (write != null)
            harmony.Patch(write, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(WriteLoadedModsToFilePrefix)));

        _installed = true;
        GD.Print("[ModFramework] Native ModManager turned off (custom loader in charge); LoadAllModManifests + read/write neutered");
    }

    // No-op bridges retained so existing call sites in ModManager.cs continue
    // to compile and behave correctly. Returning true means "no native conflict"
    // — our internal state is the source of truth.
    public static bool EnableMod(ModManifest manifest) => true;
    public static bool DisableMod(ModManifest manifest) => true;
    public static bool IsEnabled(ModManifest manifest) => false; // we manage enabled state ourselves; don't conflate with native
    public static bool CanResolveManifest(ModManifest manifest) => true; // any mod our framework knows about is valid

    public static HashSet<string> ReadPhysicalEnabledDirectories()
    {
        // Native ModManager's enabled_mods.json may still exist from a prior
        // session before we patched things; read it defensively so FrameworkModStateStore
        // can offer a "migrate from native" path if needed. Returns empty set when
        // the file isn't present (the new normal once we're in charge).
        var path = GetPhysicalEnabledModsFilePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var directories = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), BubbleJson.Options)
                ?? new List<string>();
            return new HashSet<string>(
                ModManifestJson.NormalizeIdentifiers(directories),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to read built-in enabled_mods.json: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // Prefix: skip native LoadAllModManifests entirely. Signature mirrors
    // Pratfall's:  static void LoadAllModManifests(Action onComplete).
    // We must still invoke onComplete so any waiting game code doesn't hang.
    private static bool LoadAllModManifestsPrefix(Action onComplete)
    {
        try { onComplete?.Invoke(); }
        catch (Exception ex) { GD.PrintErr($"[ModFramework] LoadAllModManifests onComplete threw: {ex.GetType().Name}: {ex.Message}"); }
        return false; // skip original
    }

    // Defensive: native ReadLoadedModsFromFile returns List<string>. We never
    // want the native loader to act on real values, so always return empty.
    private static bool ReadLoadedModsFromFilePrefix(ref List<string> __result)
    {
        __result = new List<string>();
        if (!_hasLoggedReadInterception)
        {
            _hasLoggedReadInterception = true;
            GD.Print("[ModFramework] Native ReadLoadedModsFromFile intercepted (empty list returned)");
        }
        return false;
    }

    // Defensive: native WriteLoadedModsToFile returns void. Earlier code in
    // this file declared `ref bool __result` which was wrong — that's been
    // failing silently during patch install for who knows how long. Correct
    // signature now.
    private static bool WriteLoadedModsToFilePrefix()
    {
        return false; // skip original
    }

    private static string? GetPhysicalEnabledModsFilePath()
    {
        var gameDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (string.IsNullOrWhiteSpace(gameDir))
            return null;

        return Path.Combine(gameDir, "mods", "enabled_mods.json");
    }

    private static class BubbleJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
