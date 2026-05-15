using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using Godot;
using HarmonyLib;

namespace PratfallModFramework;

public static class ModExceptionFilter
{
    private static bool _patched;
    private static readonly HashSet<string> KnownAssemblyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "0Harmony",
        "PratfallModFramework"
    };

    public static void RegisterKnownModAssembly(string assemblySimpleName)
    {
        if (!string.IsNullOrWhiteSpace(assemblySimpleName))
            KnownAssemblyNames.Add(assemblySimpleName);
    }

    public static void Install()
    {
        if (_patched) return;

        var gameAlc = AssemblyLoadContext.GetLoadContext(typeof(ModExceptionFilter).Assembly);
        if (gameAlc == null) return;

        // Find Log.OnException(object, FirstChanceExceptionEventArgs)
        MethodInfo? onException = null;
        foreach (var asm in gameAlc.Assemblies)
        {
            if (asm.GetName().Name != "Pratfall") continue;
            foreach (var t in asm.GetTypes())
            {
                if (t.Name != "Log") continue;
                var mi = t.GetMethod("OnException", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    [typeof(object), typeof(FirstChanceExceptionEventArgs)]);
                if (mi != null)
                {
                    onException = mi;
                    break;
                }
            }
            if (onException != null) break;
        }

        if (onException == null)
        {
            GD.PrintErr("[ModFramework] Could not find Log.OnException to patch");
            return;
        }

        var harmony = new Harmony("PratfallModFramework.ExceptionFilter");
        harmony.Patch(onException, prefix: new HarmonyMethod(typeof(ModExceptionFilter), nameof(OnExceptionPrefix)));
        _patched = true;
        GD.Print("[ModFramework] Installed exception filter (mod exceptions suppressed from analytics)");
    }

    private static bool OnExceptionPrefix(object sender, FirstChanceExceptionEventArgs e)
    {
        var ex = e?.Exception;
        if (ex is System.IO.FileNotFoundException fnf && fnf.FileName != null)
        {
            var assemblyName = Path.GetFileNameWithoutExtension(fnf.FileName);
            if (!string.IsNullOrEmpty(assemblyName) && KnownAssemblyNames.Contains(assemblyName))
            {
                return false;
            }
        }
        return true;
    }
}
