using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PratfallModFramework.Shared;

internal enum RestoreDecision { Restore, SkipAlreadyVanilla, Refuse }

internal sealed record DllInspection(bool Readable, bool Patched, Guid Mvid);

// ---- Stale-backup protection (shared by the installer GUI + CLI patcher) ----
// MVID is preserved through Cecil patching, so it identifies the game BUILD across
// patched/vanilla copies. We never back up a patched DLL, refresh the backup when the
// game build changes, and refuse to restore a backup that can't be proven to match the
// currently-patched build (the user falls back to Steam Verify). This guards the case
// where Steam ships a new build between install and uninstall — restoring a stale backup
// would silently downgrade the game.
//
// Logging is injected (Action<string>) so each tool routes to its own sink (the WinForms
// log box vs Console). Linked into both projects via <Compile Include ... Link>, so each
// assembly compiles its own copy — no shared assembly dependency.
internal static class BackupSafety
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    // Read-only Cecil inspection: is GcManager._Ready injected, and what's the module
    // MVID? Reading the first instruction + MVID needs no assembly resolver.
    internal static DllInspection InspectDll(string dllPath)
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
            return new DllInspection(true, patched, mvid);
        }
        catch
        {
            return new DllInspection(false, false, Guid.Empty);
        }
    }

    internal static string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static string SidecarPath(string backupPath) => backupPath + ".meta.json";

    internal static void WriteBackupSidecar(string backupPath, Action<string> warn)
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
            warn($"WARN: could not write backup metadata: {ex.Message}");
        }
    }

    internal static bool TryReadSidecarSha(string backupPath, out string sha)
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
    internal static void EnsureVanillaBackup(string dllPath, string backupPath, Action<string> log, Action<string> warn)
    {
        var live = InspectDll(dllPath);
        if (!live.Readable)
        {
            warn("WARN: could not read Pratfall.dll to check patch state; leaving any existing backup untouched.");
            return;
        }
        if (live.Patched)
        {
            if (!File.Exists(backupPath))
                // Continuing would leave a patched DLL with no recoverable vanilla — an
                // install that can never be cleanly undone. Refuse and send the user to
                // Steam Verify (which restores vanilla, after which a normal install works).
                throw new InvalidOperationException(
                    "Pratfall.dll is already patched, but no vanilla backup (.original) exists — " +
                    "a reinstall now could never be cleanly undone. Use Steam -> Verify integrity " +
                    "of game files to restore vanilla Pratfall.dll, then run the installer again.");
            log("Pratfall.dll already patched; keeping existing vanilla backup.");
            return;
        }
        if (!File.Exists(backupPath))
        {
            File.Copy(dllPath, backupPath, overwrite: false);
            WriteBackupSidecar(backupPath, warn);
            log("Backup created: Pratfall.dll.original");
            return;
        }
        var backup = InspectDll(backupPath);
        if (backup.Readable && backup.Mvid == live.Mvid)
        {
            if (!File.Exists(SidecarPath(backupPath)))
                WriteBackupSidecar(backupPath, warn); // backfill metadata for a legacy backup
            log("Existing backup matches current game build; keeping it.");
            return;
        }
        File.Copy(dllPath, backupPath, overwrite: true);
        WriteBackupSidecar(backupPath, warn);
        log("Game build changed since last backup; refreshed Pratfall.dll.original to current vanilla.");
    }

    // Uninstall-side: only restore a backup we can prove is the unpatched vanilla of
    // the currently-patched build. Otherwise refuse and point the user at Steam Verify.
    internal static RestoreDecision ValidateRestore(string dllPath, string backupPath, out string message)
    {
        var live = InspectDll(dllPath);
        if (!live.Readable)
        {
            message = "Could not read Pratfall.dll to verify patch state. Refusing to restore — use Steam -> Verify integrity.";
            return RestoreDecision.Refuse;
        }
        if (!live.Patched)
        {
            message = "Pratfall.dll is already unpatched (vanilla) — nothing to restore; leaving it as-is.";
            return RestoreDecision.SkipAlreadyVanilla;
        }
        if (!File.Exists(backupPath))
        {
            message = "No backup found. Use Steam -> Verify integrity to restore vanilla Pratfall.dll.";
            return RestoreDecision.Refuse;
        }
        var backup = InspectDll(backupPath);
        if (!backup.Readable)
        {
            message = "Backup is unreadable. Refusing to restore — use Steam -> Verify integrity.";
            return RestoreDecision.Refuse;
        }
        if (backup.Patched)
        {
            message = "Backup is itself patched (not vanilla). Refusing to restore — use Steam -> Verify integrity.";
            return RestoreDecision.Refuse;
        }
        if (TryReadSidecarSha(backupPath, out var recordedSha)
            && !string.Equals(recordedSha, Sha256Hex(backupPath), StringComparison.OrdinalIgnoreCase))
        {
            message = "Backup failed its integrity check (hash mismatch vs recorded metadata). Refusing to restore — use Steam -> Verify integrity.";
            return RestoreDecision.Refuse;
        }
        if (backup.Mvid != live.Mvid)
        {
            message = "Backup is from a different game build than the installed patch (MVID mismatch); restoring it would downgrade the game. Refusing — use Steam -> Verify integrity.";
            return RestoreDecision.Refuse;
        }
        message = "backup verified: unpatched vanilla, build matches the installed patch";
        return RestoreDecision.Restore;
    }
}
