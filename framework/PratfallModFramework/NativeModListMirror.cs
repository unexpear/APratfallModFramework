using System.Reflection;
using Godot;

namespace PratfallModFramework;

// Mirrors our scanned mod manifests into the native ModManager.Mods / LoadedMods
// lists so that vanilla mods written against Tim's recommended pattern
// (`ModManager.Mods[i].Directory`, `.Name`, `.IsSteamWorkshopMod`, etc.) still
// work under our framework.
//
// Background: post-2026-05-18 Pratfall exposes `public static List<ModManifest> Mods`
// on the native ModManager and Tim publicly recommends mod authors enumerate it.
// Our "turn off" of native LoadAllModManifests (see OfficialModBridge) would otherwise
// leave that list as the empty `new List<ModManifest>(32)` from native cctor and break
// any vanilla mod that reads it.
//
// What we sync:
//   - Discovered manifests       -> native ModManager.Mods (live list, .Clear + .Add)
//   - Per-loaded mod assemblies  -> native ModManifest.LoadedAssembly
//   - Directories of enabled mods -> native ModManager.LoadedMods (so vanilla
//                                    IsModEnabled / EnabledModCount agree with us)
//
// We mutate the existing live lists rather than replacing them via the setters,
// matching native cctor's pattern. Anything that already grabbed a reference
// continues to see updates.
//
// Reflection nowhere — we reference Pratfall.dll directly, so global::ModManager
// and global::ModManifest are typed. Sync wraps in try/catch and logs once on
// failure (vanilla pattern silently degrades; framework still loads its own mods).
internal static class NativeModListMirror
{
    private static bool _failureLogged;

    // Rebuilds native ModManager.Mods + LoadedMods from our state.
    //
    // manifests        — current scanned mod list (our schema)
    // enabledModIds    — IDs of mods our framework currently considers enabled
    //                    (i.e. _modEnabled[id] == true); maps to native LoadedMods
    // loadedAssemblies — optional id->Assembly map for mods whose DLL the framework
    //                    has actually loaded; populates native ModManifest.LoadedAssembly
    //                    so vanilla code that does `mod.LoadedAssembly?.GetType(...)`
    //                    sees the real handle
    public static void Sync(
        IReadOnlyList<ModManifest> manifests,
        IReadOnlyCollection<string> enabledModIds,
        IReadOnlyDictionary<string, Assembly>? loadedAssemblies = null)
    {
        try
        {
            // Both lists are initialized in native ModManager.cctor (verified via
            // Cecil dump). We MUTATE the live lists in place rather than reassign,
            // so anything that already grabbed a reference keeps seeing updates.
            // Mods.set is private and LoadedMods is readonly anyway — assignment
            // would need reflection, which we deliberately avoid.
            //
            // If a future Pratfall change drops the cctor init and these come
            // back null, NullReferenceException falls into the catch below and
            // we log once + the framework loader keeps working.

            // --- Mods list (all discovered) ---
            var nativeMods = global::ModManager.Mods;
            nativeMods.Clear();
            foreach (var ours in manifests)
            {
                var native = ToNative(ours);
                if (loadedAssemblies != null && loadedAssemblies.TryGetValue(ours.Id, out var asm))
                    native.LoadedAssembly = asm;
                nativeMods.Add(native);
            }

            // --- LoadedMods (directory strings of currently-enabled mods) ---
            // Native EnabledModCount = LoadedMods.Count, and IsModEnabled checks
            // membership by Directory string. Keeping this populated means vanilla
            // code paths that bypass our Harmony EnabledModCount patch (e.g. a
            // future mod calling `ModManager.LoadedMods.Contains(dir)`) get the
            // right answer too.
            var nativeLoaded = global::ModManager.LoadedMods;
            nativeLoaded.Clear();
            foreach (var ours in manifests)
            {
                if (enabledModIds.Contains(ours.Id) && !string.IsNullOrEmpty(ours.DirectoryPath))
                    nativeLoaded.Add(ours.DirectoryPath);
            }
        }
        catch (Exception ex)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                GD.PrintErr($"[ModFramework] NativeModListMirror.Sync failed (vanilla ModManager.Mods may be empty/stale; framework loader still works): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Convert our richer manifest into the shape native ModManager.Mods expects.
    // Property names match Pratfall's ModManifest as of 2026-05-18 (verified via
    // Cecil dump in tmp/dump-modmgr).
    //
    // Notes:
    //  - AutoLoad = false: we never let native LoadAutoLoadMods enable anything;
    //    the user-check gate is the single source of truth. (LoadAutoLoadMods
    //    isn't called anyway since we skip LoadAllModManifests, but defense in
    //    depth — a future Pratfall change might call it from elsewhere.)
    //  - PackageName = our PckFile (native treats it as the .pck filename).
    //  - SteamWorkshopManifest / SteamWorkshopItem deliberately left null:
    //    we don't parse the native Workshop manifest schema, and SteamWorkshopItem
    //    needs a live Steamworks query we don't currently do. Vanilla mods can
    //    still gate on IsSteamWorkshopMod = true.
    //  - Tags: empty array — our manifest schema doesn't have a Tags field today.
    //    If we ever add one, just thread it through here.
    private static global::ModManifest ToNative(ModManifest ours)
    {
        return new global::ModManifest
        {
            Name = ours.Name,
            Version = ours.Version,
            Description = ours.Description,
            Author = ours.Author,
            Tags = Array.Empty<string>(),
            PackageName = ours.PckFile,
            Assembly = ours.AssemblyFile,
            // NOTE: the game's ModManifest dropped its `AddAssemblyToGodot` field in the
            // 2026-06 big update (Steam build 23570525) — script-bridge registration is now
            // unconditional in ModManager.LoadAssembly, so there's no native field to mirror.
            // Our own ModManifest.AddAssemblyToGodot still gates OUR loader (ModAssemblyLoader);
            // it's just no longer copied onto the native mirror object.
            AutoLoad = false,
            Directory = ours.DirectoryPath,
            DirectoryName = ours.DirectoryName,
            IsSteamWorkshopMod = ours.IsSteamWorkshopMod,
        };
    }
}
