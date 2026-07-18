using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Godot;
using HarmonyLib;

namespace PratfallModFramework;

public class ModAssemblyLoader
{
    private static readonly HashSet<string> SharedAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "0Harmony",
        "Facepunch.Steamworks.Win64",
        "GodotSharp",
        "Pratfall",
        "PratfallModFramework"
    };

    private readonly List<LoadedMod> _loaded = new();

    static ModAssemblyLoader()
    {
        EnsureHarmonyLoaded();
    }

    public Assembly LoadMod(string id, string assemblyPath, string? expectedSha256Hex = null, bool addAssemblyToGodot = true)
    {
        UnloadMod(id);
        EnsureHarmonyLoaded();

        // If the manifest pins a hash, verify before loading. Mismatch = refuse to load,
        // protecting against tampering and stale files. Empty pin = back-compat (load any).
        if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
        {
            var actual = ComputeFileSha256Hex(assemblyPath);
            if (!string.Equals(actual, expectedSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"DLL hash mismatch for mod {id}: manifest pins {expectedSha256Hex}, on-disk is {actual}");
            }
            Log($"[ModFramework] Verified mod {id} DLL against manifest sha256");
        }

        var alc = new ModLoadContext(assemblyPath);
        var assembly = alc.LoadFromAssemblyPath(assemblyPath);

        // Register the assembly with Godot's script bridge so mod-defined Node /
        // Resource types are usable from .tscn / PackedScene.Instantiate. Matches
        // the official loader's behavior; opt-out via manifest.AddAssemblyToGodot=false.
        // Wrapped because a registration failure shouldn't take the whole mod down —
        // patches and OnLoad may still work.
        if (addAssemblyToGodot)
        {
            try { Godot.Bridge.ScriptManagerBridge.LookupScriptsInAssembly(assembly); }
            catch (Exception ex) { LogError($"[ModFramework] Failed to register {id} scripts with Godot: {ex.Message}"); }
        }

        var harmony = new Harmony(id);
        int patchesApplied = ApplyDeclaredPatches(id, assembly, harmony);

        List<MethodInfo> unloadCallbacks;
        try
        {
            unloadCallbacks = InvokeLoadCallbacks(assembly);
        }
        catch (Exception ex)
        {
            // Fail closed: a mod whose OnLoad throws must not survive as a partial load.
            // Patches were already applied above, so unpatch them, drop the collectible
            // assembly context, and never record the mod — otherwise ModManager would still
            // mark it enabled and advertise it to peers. Report once here against the mod's
            // real exception (reflection wraps it); ModOnLoadException tells ModManager's
            // load catch not to write a second report. Mirrors UnloadMod's teardown.
            var real = (ex as TargetInvocationException)?.InnerException ?? ex;
            harmony.UnpatchAll(harmony.Id);
            alc.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            LogError($"[ModFramework] OnLoad failed for mod {id}; rolled back {patchesApplied} patch(es) + assembly load");
            ModCrashReporter.Report(id, "OnLoad", real);
            throw new ModOnLoadException(id, real);
        }

        _loaded.Add(new LoadedMod(id, alc, harmony, assembly, patchesApplied, unloadCallbacks));
        Log($"[ModFramework] Loaded mod {id} ({patchesApplied} patches)");
        return assembly;
    }

    private static string ComputeFileSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static void EnsureHarmonyLoaded()
    {
        if (FindLoadedAssembly("0Harmony") != null)
            return;

        var frameworkAssembly = typeof(ModAssemblyLoader).Assembly;
        var hostContext = AssemblyLoadContext.GetLoadContext(frameworkAssembly);
        if (hostContext == null)
            return;

        try
        {
            using var stream = frameworkAssembly.GetManifestResourceStream("0Harmony.dll");
            if (stream != null)
            {
                hostContext.LoadFromStream(stream);
                System.Console.WriteLine("[ModFramework] Loaded embedded 0Harmony into host context");
                return;
            }

            var frameworkDir = Path.GetDirectoryName(frameworkAssembly.Location);
            if (frameworkDir == null)
                return;

            var diskPath = Path.Combine(frameworkDir, "0Harmony.dll");
            if (File.Exists(diskPath))
            {
                hostContext.LoadFromAssemblyPath(diskPath);
                System.Console.WriteLine("[ModFramework] Loaded 0Harmony.dll from framework directory");
            }
            else
            {
                System.Console.Error.WriteLine("[ModFramework] 0Harmony.dll not found as embedded resource or sidecar");
            }
        }
        catch (Exception ex)
        {
            System.Console.Error.WriteLine($"[ModFramework] Failed to ensure 0Harmony is loaded: {ex.Message}");
        }
    }

    private static int ApplyDeclaredPatches(string id, Assembly asm, Harmony harmony)
    {
        int patchesApplied = 0;

        foreach (var type in asm.GetTypes())
        {
            foreach (var attr in type.GetCustomAttributes<ModPatchAttribute>())
            {
                var prefix = CreatePatchMethod(type, attr.Type, PatchType.Prefix, "Prefix", id);
                var postfix = CreatePatchMethod(type, attr.Type, PatchType.Postfix, "Postfix", id);
                var transpiler = CreatePatchMethod(type, attr.Type, PatchType.Transpiler, "Transpiler", id);

                var target = AccessTools.Method(attr.TargetType, attr.MethodName);
                if (target == null)
                {
                    LogError($"[ModFramework] Target {attr.TargetType.FullName}.{attr.MethodName} not found for mod {id}");
                    continue;
                }

                harmony.Patch(target, prefix: prefix, postfix: postfix, transpiler: transpiler);
                patchesApplied++;
                Log($"[ModFramework] Patched {attr.TargetType.Name}.{attr.MethodName} ({attr.Type}) for mod {id}");
            }
        }

        return patchesApplied;
    }

    private static HarmonyMethod? CreatePatchMethod(
        Type type,
        PatchType requestedType,
        PatchType targetType,
        string methodName,
        string modId)
    {
        if (requestedType != targetType)
            return null;

        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null)
        {
            LogError($"[ModFramework] {targetType} patch method {type.FullName}.{methodName} not found for mod {modId}");
            return null;
        }

        return new HarmonyMethod(method);
    }

    // Invokes each type's static OnLoad and collects static OnUnload methods. A throwing
    // OnLoad is NOT swallowed — it propagates so LoadMod can fail the load closed (roll back
    // patches + assembly, never record the mod). Reflection wraps the mod's real exception in
    // a TargetInvocationException; LoadMod unwraps it for the single crash report.
    private static List<MethodInfo> InvokeLoadCallbacks(Assembly asm)
    {
        var unloadCallbacks = new List<MethodInfo>();

        foreach (var type in asm.GetTypes())
        {
            var onLoad = type.GetMethod("OnLoad", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (onLoad != null && onLoad.GetParameters().Length == 0 && ModRuntime.IsGodotRuntimeReady)
                onLoad.Invoke(null, null);

            var onUnload = type.GetMethod("OnUnload", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (onUnload != null && onUnload.GetParameters().Length == 0)
                unloadCallbacks.Add(onUnload);
        }

        return unloadCallbacks;
    }

    private static Assembly? FindLoadedAssembly(string simpleName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(assembly.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return assembly;
        }

        return null;
    }

    public void UnloadMod(string id)
    {
        var entry = _loaded.Find(e => e.Id == id);
        if (entry == null) return;

        if (ModRuntime.IsGodotRuntimeReady)
        {
            foreach (var callback in entry.OnUnloadCallbacks)
            {
                try
                {
                    callback.Invoke(null, null);
                }
                catch (Exception ex)
                {
                    LogError($"[ModFramework] OnUnload failed for mod {id}: {ex.InnerException?.Message ?? ex.Message}");
                    ModCrashReporter.Report(id, $"OnUnload in {callback.DeclaringType?.FullName ?? "<unknown>"}", ex.InnerException ?? ex);
                }
            }
        }

        // Force-clean any ILifecycleHandler this mod registered with the game's
        // LifecycleManager but didn't free in OnUnload: a leaked handler keeps getting
        // OnUpdate after disable AND pins this ALC (blocking unload). Targeted by load
        // context so we never touch the game's own handlers or another mod's. Runs before
        // Context.Unload() so freeing the nodes also drops the refs that would pin it.
        FreeLeakedLifecycleHandlers(id, entry.Context);

        entry.Harmony.UnpatchAll(entry.Harmony.Id);
        entry.Context.Unload();
        _loaded.Remove(entry);
        Log($"[ModFramework] Unloaded mod {id}");
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    // Unregister + free ILifecycleHandler instances whose type was loaded into this mod's
    // ALC. Mirrors the Harmony UnpatchAll above: makes disable reliably stop a mod even when
    // the author forgot to QueueFree its node. Best-effort and fully contained — never breaks
    // the unload path. NOT a sandbox: a mod can still persist via other vectors (its own
    // Harmony instance, static event subscriptions, threads); this just closes the easy,
    // common ILifecycleHandler hole. Only framework-loaded mods reach here — native-loader
    // mods are the native loader's responsibility (same as a no-framework install).
    private void FreeLeakedLifecycleHandlers(string id, AssemblyLoadContext ctx)
    {
        if (!ModRuntime.IsGodotRuntimeReady) return;
        try
        {
            var mgr = LifecycleManager.Instance;
            if (mgr == null) return;

            // Build 24260516 ("Gumballs & Glory") refactored LifecycleManager to a queue-command
            // model and dropped GetHandlers()/Unregister(). Reflect for them so the framework still
            // compiles + loads: on builds that expose them we do the full leaked-handler sweep; on
            // newer builds this degrades to a no-op (hardening skipped, but mod unload still proceeds).
            var mt = mgr.GetType();
            if (mt.GetMethod("GetHandlers", System.Type.EmptyTypes)?.Invoke(mgr, null)
                is not System.Collections.IEnumerable live) return;
            var unregister = mt.GetMethod("Unregister", new[] { typeof(ILifecycleHandler) });

            // Snapshot first — Unregister mutates the manager's live list.
            var doomed = new List<ILifecycleHandler>();
            foreach (var o in live)
            {
                if (o is not ILifecycleHandler h) continue;
                if (AssemblyLoadContext.GetLoadContext(h.GetType().Assembly) == ctx)
                    doomed.Add(h);
            }

            foreach (var h in doomed)
            {
                try { unregister?.Invoke(mgr, new object[] { h }); } catch { /* best-effort */ }
                if (h is Node node && GodotObject.IsInstanceValid(node))
                    node.QueueFree();
            }

            if (doomed.Count > 0)
                Log($"[ModFramework] Force-freed {doomed.Count} leaked lifecycle handler(s) from {id} on disable (mod skipped its own cleanup)");
        }
        catch (Exception ex)
        {
            LogError($"[ModFramework] Lifecycle-handler cleanup for {id} failed (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }
    }

    public bool IsLoaded(string id) => _loaded.Any(e => e.Id == id);

    // Snapshot of currently-loaded mod assemblies, keyed by mod id. Used by the
    // compatibility checker to scan for Harmony patch overlaps without holding a
    // strong reference to the loader's internal state.
    public IReadOnlyDictionary<string, Assembly> SnapshotLoadedAssemblies()
    {
        var dict = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _loaded)
            dict[entry.Id] = entry.Assembly;
        return dict;
    }

    private sealed class ModLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ModLoadContext(string mainAssemblyPath) : base(Path.GetFileNameWithoutExtension(mainAssemblyPath), isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name != null && SharedAssemblyNames.Contains(assemblyName.Name))
                return FindLoadedAssembly(assemblyName.Name);

            var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
                return LoadFromAssemblyPath(assemblyPath);

            return null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
                return LoadUnmanagedDllFromPath(libraryPath);

            return base.LoadUnmanagedDll(unmanagedDllName);
        }
    }

    private sealed record LoadedMod(
        string Id,
        AssemblyLoadContext Context,
        Harmony Harmony,
        Assembly Assembly,
        int PatchesApplied,
        List<MethodInfo> OnUnloadCallbacks);

    private static void Log(string message)
    {
        if (ModRuntime.IsGodotRuntimeReady)
            GD.Print(message);
        else
            Console.WriteLine(message);
    }

    private static void LogError(string message)
    {
        if (ModRuntime.IsGodotRuntimeReady)
            GD.PrintErr(message);
        else
            Console.Error.WriteLine(message);
    }
}

// Thrown by ModAssemblyLoader.LoadMod when a mod's OnLoad throws. By the time this is raised
// the loader has already reported the crash and rolled back (unpatched + unloaded the
// assembly), so ModManager's load catch marks the mod disabled WITHOUT writing a second report.
public sealed class ModOnLoadException : Exception
{
    public string ModId { get; }

    public ModOnLoadException(string modId, Exception inner)
        : base($"OnLoad failed for mod {modId}: {inner.Message}", inner) => ModId = modId;
}
