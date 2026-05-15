using System.Reflection;
using System.Runtime.Loader;

namespace PratfallBootstrapLoader;

/// <summary>
/// Zero-dependency loader that bootstraps the Mod Framework into the game's
/// AssemblyLoadContext so GodotSharp dependencies resolve correctly.
/// </summary>
public static class Loader
{
    public static void Init(string frameworkDllPath)
    {
        try
        {
            // Find the game's assembly to get its AssemblyLoadContext
            var gameAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Pratfall");

            if (gameAssembly == null)
            {
                System.Console.Error.WriteLine("[BootstrapLoader] Could not find Pratfall assembly");
                return;
            }

            var gameContext = AssemblyLoadContext.GetLoadContext(gameAssembly);
            if (gameContext == null)
            {
                System.Console.Error.WriteLine("[BootstrapLoader] Could not get game's AssemblyLoadContext");
                return;
            }

            // Load the framework DLL into the game's context (same context as GodotSharp)
            var frameworkAsm = gameContext.LoadFromAssemblyPath(frameworkDllPath);

            // Find and invoke Bootstrap.Init()
            var bootstrapType = frameworkAsm.GetType("PratfallModFramework.Bootstrap");
            if (bootstrapType == null)
            {
                System.Console.Error.WriteLine("[BootstrapLoader] Could not find PratfallModFramework.Bootstrap type");
                return;
            }

            var initMethod = bootstrapType.GetMethod("Init", BindingFlags.Public | BindingFlags.Static);
            if (initMethod == null)
            {
                System.Console.Error.WriteLine("[BootstrapLoader] Could not find Bootstrap.Init() method");
                return;
            }

            System.Console.WriteLine("[BootstrapLoader] Invoking Bootstrap.Init()");
            initMethod.Invoke(null, null);
            System.Console.WriteLine("[BootstrapLoader] Bootstrap.Init() returned successfully");
        }
        catch (System.Exception ex)
        {
            System.Console.Error.WriteLine($"[BootstrapLoader] ERROR: {ex.GetType().Name}: {ex.Message}");
            System.Console.Error.WriteLine(ex.StackTrace);
        }
    }
}
