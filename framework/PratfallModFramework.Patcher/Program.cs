using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

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

        EnsureVanillaBackup(DllPath, BackupPath);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var frameworkSrc = Path.Combine(baseDir, "PratfallModFramework.dll");
        if (File.Exists(frameworkSrc))
        {
            File.Copy(frameworkSrc, FrameworkDllDest, overwrite: true);
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
        var decision = ValidateRestore(DllPath, BackupPath, out var msg);
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
            DeleteIfExists(SidecarPath(BackupPath));
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

    private enum RestoreDecision { Restore, SkipAlreadyVanilla, Refuse }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Read-only Cecil inspection: is GcManager._Ready injected, and what's the module
    // MVID? MVID is preserved through Cecil patching, so it identifies the game build.
    private static (bool Readable, bool Patched, Guid Mvid) InspectDll(string dllPath)
    {
        try
        {
            using var asm = AssemblyDefinition.ReadAssembly(dllPath);
            var mvid = asm.MainModule.Mvid;
            var ready = asm.MainModule.Types.FirstOrDefault(t => t.Name == "GcManager")?
                .Methods.FirstOrDefault(m => m.Name == "_Ready");
            var insns = ready?.Body?.Instructions;
            var patched = insns is { Count: > 0 }
                && insns[0].OpCode == OpCodes.Ldstr
                && insns[0].Operand?.ToString()?.Contains("PratfallBootstrapLoader") == true;
            return (true, patched, mvid);
        }
        catch
        {
            return (false, false, Guid.Empty);
        }
    }

    private static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string SidecarPath(string backupPath) => backupPath + ".meta.json";

    private static void WriteBackupSidecar(string backupPath)
    {
        try
        {
            var meta = new
            {
                sha256 = Sha256Hex(backupPath),
                mvid = InspectDll(backupPath).Mvid.ToString(),
                createdUtc = DateTime.UtcNow.ToString("o"),
                patcherVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            };
            File.WriteAllText(SidecarPath(backupPath), JsonSerializer.Serialize(meta, IndentedJson));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARN: could not write backup metadata: {ex.Message}");
        }
    }

    private static bool TryReadSidecarSha(string backupPath, out string sha)
    {
        sha = "";
        try
        {
            var path = SidecarPath(backupPath);
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("sha256", out var s))
            {
                sha = s.GetString() ?? "";
                return sha.Length > 0;
            }
            return false;
        }
        catch { return false; }
    }

    // Install-side: never back up a patched DLL; (re)create the backup only from
    // unpatched vanilla, and refresh it when the game build (MVID) has changed.
    private static void EnsureVanillaBackup(string dllPath, string backupPath)
    {
        var live = InspectDll(dllPath);
        if (!live.Readable)
        {
            Console.Error.WriteLine("WARN: could not read Pratfall.dll to check patch state; leaving any existing backup untouched.");
            return;
        }
        if (live.Patched)
        {
            if (!File.Exists(backupPath))
                Console.Error.WriteLine("WARN: Pratfall.dll is already patched but no vanilla backup exists. Use Steam -> Verify integrity, then reinstall.");
            else
                Console.WriteLine("Pratfall.dll already patched; keeping existing vanilla backup.");
            return;
        }
        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, overwrite: false);
            WriteBackupSidecar(backupPath);
            Console.WriteLine("Backup created: Pratfall.dll.original");
            return;
        }
        var backup = InspectDll(backupPath);
        if (backup.Readable && backup.Mvid == live.Mvid)
        {
            if (!File.Exists(SidecarPath(backupPath)))
                WriteBackupSidecar(backupPath); // backfill metadata for a legacy backup
            Console.WriteLine("Existing backup matches current game build; keeping it.");
            return;
        }
        File.Copy(dllPath, backupPath, overwrite: true);
        WriteBackupSidecar(backupPath);
        Console.WriteLine("Game build changed since last backup; refreshed Pratfall.dll.original to current vanilla.");
    }

    // Uninstall-side: only restore a backup we can prove is the unpatched vanilla of
    // the currently-patched build. Otherwise refuse and point the user at Steam Verify.
    private static RestoreDecision ValidateRestore(string dllPath, string backupPath, out string message)
    {
        var live = InspectDll(dllPath);
        if (!live.Readable)
        {
            message = "could not read Pratfall.dll to verify patch state — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        if (!live.Patched)
        {
            message = "Pratfall.dll is already unpatched (vanilla) — nothing to restore.";
            return RestoreDecision.SkipAlreadyVanilla;
        }
        if (!File.Exists(backupPath))
        {
            message = "no backup found — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        var backup = InspectDll(backupPath);
        if (!backup.Readable)
        {
            message = "backup unreadable — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        if (backup.Patched)
        {
            message = "backup is itself patched (not vanilla) — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        if (TryReadSidecarSha(backupPath, out var recordedSha)
            && !string.Equals(recordedSha, Sha256Hex(backupPath), StringComparison.OrdinalIgnoreCase))
        {
            message = "backup failed integrity check (hash mismatch) — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        if (backup.Mvid != live.Mvid)
        {
            message = "backup is from a different game build (MVID mismatch); restoring would downgrade — use Steam -> Verify integrity";
            return RestoreDecision.Refuse;
        }
        message = "backup verified: unpatched vanilla, build matches installed patch";
        return RestoreDecision.Restore;
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
