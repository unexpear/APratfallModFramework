using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;
using PratfallModFramework.Shared;

namespace PratfallModFramework.Patcher;

/// <summary>
/// Patches Pratfall.dll to load the Mod Framework at game startup.
/// Creates a backup before modifying. Fully reversible.
///
/// How it works:
///   Finds GcManager._Ready() (a very early Godot autoload) and injects
///   code at its beginning that loads PratfallModFramework.dll via
///   Assembly.LoadFile and calls Bootstrap.Init().
///
///   No module initializer needed — this runs at Godot's _Ready phase,
///   when the SceneTree is already available.
///
/// Uninstall: Run with "uninstall" arg, or Steam Verify Integrity.
/// </summary>
public static class Program
{
    private static readonly string GameDir = @"D:\SteamLibrary\steamapps\common\Pratfall";
    private static readonly string DataDir = Path.Combine(GameDir, "data_Pratfall_windows_x86_64");
    private static readonly string DllPath = Path.Combine(DataDir, "Pratfall.dll");
    private static readonly string BackupPath = Path.Combine(DataDir, "Pratfall.dll.original");
    private static readonly string FrameworkDllDest = Path.Combine(DataDir, "PratfallModFramework.dll");

    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "uninstall")
        {
            Uninstall();
            return;
        }

        if (!File.Exists(DllPath))
        {
            Console.Error.WriteLine($"Pratfall.dll not found at {DllPath}");
            return;
        }

        try
        {
            BackupSafety.EnsureVanillaBackup(DllPath, BackupPath, Console.WriteLine, s => Console.Error.WriteLine(s));
        }
        catch (InvalidOperationException ex)
        {
            // patched-with-no-backup: refuse rather than create an un-undoable install.
            Console.Error.WriteLine(ex.Message);
            Environment.ExitCode = 1;
            return;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var frameworkSrc = Path.Combine(baseDir, "PratfallModFramework.dll");
        if (File.Exists(frameworkSrc))
        {
            // Recognize an existing (possibly older) framework before overwriting, mirroring
            // the GUI installer's version-aware messaging.
            var existing = File.Exists(FrameworkDllDest) ? TryReadVersion(FrameworkDllDest) : null;
            File.Copy(frameworkSrc, FrameworkDllDest, overwrite: true);
            LogVersionTransition(existing, TryReadVersion(FrameworkDllDest));
            Console.WriteLine($"Framework copied to {FrameworkDllDest}");
        }

        var bootstrapLoaderSrc = Path.Combine(baseDir, "PratfallBootstrapLoader.dll");
        var bootstrapLoaderDst = Path.Combine(DataDir, "PratfallBootstrapLoader.dll");
        if (File.Exists(bootstrapLoaderSrc))
        {
            File.Copy(bootstrapLoaderSrc, bootstrapLoaderDst, overwrite: true);
            Console.WriteLine($"BootstrapLoader copied to {bootstrapLoaderDst}");
        }

        PatchGcManagerReady();
    }

    private static readonly string BootstrapLoaderPath = Path.Combine(DataDir, "PratfallBootstrapLoader.dll");

    private static void PatchGcManagerReady()
    {
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(DataDir);

        using var assembly = AssemblyDefinition.ReadAssembly(DllPath, new ReaderParameters
        {
            ReadWrite = true,
            AssemblyResolver = resolver
        });

        var module = assembly.MainModule;

        // Find GcManager._Ready()
        var gcManager = module.Types.FirstOrDefault(t => t.Name == "GcManager");
        if (gcManager == null)
        {
            Console.Error.WriteLine("GcManager type not found in Pratfall.dll");
            return;
        }

        var readyMethod = gcManager.Methods.FirstOrDefault(m => m.Name == "_Ready");
        if (readyMethod == null)
        {
            Console.Error.WriteLine("GcManager._Ready() method not found");
            return;
        }

        // Check if already patched
        if (readyMethod.Body.Instructions.Count > 0 &&
            readyMethod.Body.Instructions[0].OpCode == OpCodes.Ldstr &&
            readyMethod.Body.Instructions[0].Operand?.ToString()?.Contains("PratfallBootstrapLoader") == true)
        {
            Console.WriteLine("GcManager._Ready() already patched, skipping");
            return;
        }

        var il = readyMethod.Body.GetILProcessor();
        var firstInsn = readyMethod.Body.Instructions[0];

        // Inject: Load BootstrapLoader.dll (zero deps), get Loader.Init(string), invoke it
        var loadFileRef = module.ImportReference(
            typeof(Assembly).GetMethod("LoadFile", new[] { typeof(string) })!);
        var getTypeRef = module.ImportReference(
            typeof(Assembly).GetMethod("GetType", new[] { typeof(string) })!);
        var getMethodRef = module.ImportReference(
            typeof(Type).GetMethod("GetMethod", new[] { typeof(string) })!);
        var invokeRef = module.ImportReference(
            typeof(MethodInfo).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) })!);

        var objectType = module.ImportReference(typeof(object));

        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, BootstrapLoaderPath));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Call, loadFileRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, "PratfallBootstrapLoader.Loader"));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, getTypeRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, "Init"));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, getMethodRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldnull)); // target = null (static method)
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldc_I4_1)); // args array length = 1
        il.InsertBefore(firstInsn, il.Create(OpCodes.Newarr, objectType)); // new object[1]
        il.InsertBefore(firstInsn, il.Create(OpCodes.Dup));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldc_I4_0)); // index 0
        il.InsertBefore(firstInsn, il.Create(OpCodes.Ldstr, FrameworkDllDest));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Stelem_Ref)); // args[0] = frameworkPath
        il.InsertBefore(firstInsn, il.Create(OpCodes.Callvirt, invokeRef));
        il.InsertBefore(firstInsn, il.Create(OpCodes.Pop));

        assembly.Write();
        Console.WriteLine("GcManager._Ready() patched to load BootstrapLoader. Pratfall.dll modified.");
    }

    private static void Uninstall()
    {
        var decision = BackupSafety.ValidateRestore(DllPath, BackupPath, out var msg);
        if (decision == RestoreDecision.Refuse)
        {
            // Never silently restore an unverifiable backup — leave the working
            // patched install intact and tell the user to use Steam Verify.
            Console.Error.WriteLine($"Refusing to restore: {msg}");
            return;
        }
        if (decision == RestoreDecision.Restore)
        {
            File.Move(BackupPath, DllPath, overwrite: true);
            DeleteIfExists(BackupSafety.SidecarPath(BackupPath));
            Console.WriteLine($"Restored original Pratfall.dll ({msg})");
        }
        else // SkipAlreadyVanilla
        {
            Console.WriteLine(msg);
        }

        if (File.Exists(FrameworkDllDest))
        {
            File.Delete(FrameworkDllDest);
            Console.WriteLine("Removed PratfallModFramework.dll");
        }

        Console.WriteLine("Uninstall complete.");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // Major.Minor.Build off the deployed framework assembly, or null if missing/unreadable.
    private static string? TryReadVersion(string dllPath)
    {
        try
        {
            var v = System.Reflection.AssemblyName.GetAssemblyName(dllPath).Version;
            return v == null ? null : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return null; }
    }

    // Mirror the GUI installer's install/update/reinstall/downgrade reporting.
    private static void LogVersionTransition(string? existing, string? deployed)
    {
        if (deployed == null) return;
        if (existing == null) Console.WriteLine($"No prior framework detected — installed {deployed}.");
        else if (existing == deployed) Console.WriteLine($"Framework {deployed} reinstalled (same version).");
        else if (Version.TryParse(existing, out var ev) && Version.TryParse(deployed, out var dv))
            Console.WriteLine(dv > ev
                ? $"Detected older framework {existing} — updated to {deployed}."
                : $"WARNING: replaced framework {existing} with OLDER {deployed} (downgrade).");
        else Console.WriteLine($"Framework {existing} -> {deployed}.");
    }
}
