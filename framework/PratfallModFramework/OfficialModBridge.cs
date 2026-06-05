using HarmonyLib;
using Godot;

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
    private static Harmony? _harmony;
    private static bool _installed;
    private static bool _hasLoggedReadInterception;

    public static void Install()
    {
        if (_installed)
            return;

        try
        {
            _harmony = new Harmony("PratfallModFramework.OfficialModBridge");

            var loadAll = AccessTools.Method(typeof(global::ModManager), "LoadAllModManifests");
            if (loadAll != null)
            {
                _harmony.Patch(loadAll, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(LoadAllModManifestsPrefix)));
            }
            else
            {
                GD.PrintErr("[ModFramework] OfficialModBridge: LoadAllModManifests not found — Pratfall version may have changed signatures");
            }

            var read = AccessTools.Method(typeof(global::ModManager), "ReadLoadedModsFromFile");
            if (read != null)
                _harmony.Patch(read, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(ReadLoadedModsFromFilePrefix)));

            var write = AccessTools.Method(typeof(global::ModManager), "WriteLoadedModsToFile");
            if (write != null)
                _harmony.Patch(write, prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(WriteLoadedModsToFilePrefix)));

            _installed = true;
            GD.Print("[ModFramework] Native ModManager turned off (custom loader in charge); LoadAllModManifests + read/write neutered");
        }
        catch (Exception ex)
        {
            // Don't let a single patch failure (e.g. Pratfall signature drift on a
            // future update) take the framework down. We log and continue degraded —
            // the native loader will still run in parallel and may double-load or
            // mis-report, but the framework's own loader keeps working. Symptoms
            // surface as duplicate mod entries / wrong EnabledModCount; check the
            // error below to diagnose.
            GD.PrintErr($"[ModFramework] OfficialModBridge.Install failed — native ModManager NOT turned off, may interfere with framework loader: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Symmetric teardown — unpatches Harmony + resets state so a fresh Install()
    // after Shutdown re-installs cleanly. Matches the pattern WorkshopSubscriber
    // and NativeModUiSuppressor use. Safe to call multiple times; safe to call
    // when Install was never run.
    public static void Shutdown()
    {
        if (_harmony != null)
        {
            try { _harmony.UnpatchAll(_harmony.Id); }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] OfficialModBridge.Shutdown: Harmony unpatch threw: {ex.GetType().Name}: {ex.Message}");
            }
            _harmony = null;
        }
        _installed = false;
        _hasLoggedReadInterception = false;
    }

    // No-op bridges retained so existing call sites in ModManager.cs continue
    // to compile and behave correctly. Returning true means "no native conflict"
    // — our internal state is the source of truth. Note: all call sites are
    // currently gated by `ModManifest.UsesOfficialLoader()` which always returns
    // false post-2026-05-18, so these never actually execute. Both the bridges
    // and the dead call sites are queued for removal in a follow-up cleanup pass
    // (tracked in ModManifest.UsesOfficialLoader's comment).
    public static bool EnableMod(ModManifest manifest) => true;
    public static bool DisableMod(ModManifest manifest) => true;
    public static bool IsEnabled(ModManifest manifest) => false; // we manage enabled state ourselves; don't conflate with native
    public static bool CanResolveManifest(ModManifest manifest) => true; // any mod our framework knows about is valid

    // Prefix: skip native LoadAllModManifests entirely. Pratfall's real signature is
    //   static void LoadAllModManifests(bool isInitialLoad, Action onComplete)
    // (verified build 23581753; the `bool isInitialLoad` arg has been present at least
    // since 23570525 — it is NOT the 1-arg form older comments described). Harmony binds
    // our prefix's `onComplete` parameter by NAME, so we deliberately don't declare
    // `isInitialLoad` — we don't consume it. We must still invoke onComplete so any
    // waiting game code (the continuation calls LoadAutoLoadMods/LoadEnabledMods, which
    // are no-ops for us since discovery is skipped + ReadLoadedModsFromFile is neutered)
    // doesn't hang.
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
}
