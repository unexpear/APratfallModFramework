using System.IO.Compression;
using Godot;

namespace PratfallModFramework;

// Zip-drop install — auto-extract any `.zip` files dropped into our mod-scan
// roots (Thunderstore downloads, manual zip drops, GitHub release artifacts).
// Pratfall's loader doesn't handle archives; without this, a player dropping
// `MyMod.zip` into `%APPDATA%\Pratfall\mods\` would see nothing happen.
//
// Behavior per zip:
//   1. Only scan roots from EnumerateScanRoots() are inspected — Steam
//      Workshop content is deliberately excluded (Steam owns those folders
//      and Workshop downloads aren't .zip-format).
//   2. Extract to a same-named folder NEXT TO the zip (zip `MyMod.zip` →
//      folder `MyMod/` in the same parent). Skip if the destination folder
//      already exists — never overwrite an existing mod install silently.
//   3. On successful extract, delete the source zip.
//   4. On any failure, leave the zip in place + log the error; never partially
//      extract (use a temp folder + rename).
//
// Zip-slip protection: .NET 8's ZipFile.ExtractToDirectory rejects entries
// that traverse above the destination directory ("../foo" or absolute paths).
// We rely on that — no manual path-component checking needed.
//
// Called once at startup from ManifestManager.ScanLocalMods before discovery.
// Re-extraction on the same zip is impossible because the source is deleted
// on success; if extraction fails the zip persists and gets a fresh retry on
// the next launch.
internal static class ModZipDropInstaller
{
    public static void ExtractAll()
    {
        int extracted = 0, skipped = 0, failed = 0;
        foreach (var root in EnumerateScanRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var zipPath in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
                {
                    switch (TryExtractOne(zipPath))
                    {
                        case ExtractResult.Extracted: extracted++; break;
                        case ExtractResult.Skipped:   skipped++;   break;
                        case ExtractResult.Failed:    failed++;    break;
                    }
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] ModZipDropInstaller: enumerate failed for {root}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (extracted + skipped + failed > 0)
            GD.Print($"[ModFramework] ModZipDropInstaller: {extracted} extracted, {skipped} skipped, {failed} failed");
    }

    private static IEnumerable<string> EnumerateScanRoots()
    {
        // Mirrors ManifestManager.ScanLocalMods scan roots minus the Steam
        // Workshop content folder (Steam owns those folders; we should never
        // write into them, and Workshop downloads aren't .zip-format).
        //
        // Dedupes against a normalized-absolute-path set so a misconfigured
        // --qh-mod-directory equal to user://mods or <game>/mods doesn't get
        // walked twice — would mean re-enumerating the same zips and getting
        // confusing "already exists" log lines on the second pass.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var profilePath = FrameworkProfile.ProfileModsDirectory;
        if (!string.IsNullOrEmpty(profilePath) && PathUtil.TryAddNormalized(seen, profilePath))
            yield return profilePath;

        var userModsAbs = ProjectSettings.GlobalizePath("user://mods/");
        if (!string.IsNullOrWhiteSpace(userModsAbs) && PathUtil.TryAddNormalized(seen, userModsAbs))
            yield return userModsAbs;

        if (!FrameworkProfile.IsActive)
        {
            var gameDir = Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(gameDir))
            {
                var gameModsDir = Path.Combine(gameDir, "mods");
                if (PathUtil.TryAddNormalized(seen, gameModsDir))
                    yield return gameModsDir;
            }
        }
    }

    private enum ExtractResult { Extracted, Skipped, Failed }

    private static ExtractResult TryExtractOne(string zipPath)
    {
        string? parent = null;
        string? folderName = null;
        try
        {
            parent = Path.GetDirectoryName(zipPath);
            folderName = Path.GetFileNameWithoutExtension(zipPath);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(folderName))
            {
                GD.PrintErr($"[ModFramework] ModZipDropInstaller: bad zip path {zipPath}");
                return ExtractResult.Failed;
            }

            var destPath = Path.Combine(parent, folderName);
            if (Directory.Exists(destPath))
            {
                // Don't overwrite an existing install — could be a different
                // version, could be the same mod with user customization. Log
                // and leave both the zip and the existing folder alone; user
                // can resolve manually if they intended an update.
                GD.Print($"[ModFramework] ModZipDropInstaller: target folder already exists, skipping ({destPath}). Remove the folder if you want the zip extracted.");
                return ExtractResult.Skipped;
            }

            // Extract to a temp folder next to the destination, then rename.
            // Means a partial / failed extraction never leaves a broken mod
            // folder in place. 8 hex chars of guid is enough collision space
            // per folderName; keeps the temp-path short so deeply-nested
            // profile paths don't trip Windows MAX_PATH (260) limit.
            var guidSuffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            var tempDest = Path.Combine(parent, $".pmfw-zipdrop-{folderName}-{guidSuffix}");
            ZipFile.ExtractToDirectory(zipPath, tempDest); // throws on zip-slip in .NET 7+
            Directory.Move(tempDest, destPath);

            // Extract succeeded — count it. If the cleanup-delete fails (file
            // lock, AV scanner, permission), the orphan zip will be skipped
            // on next launch (destPath exists check above) but we don't want
            // to roll back the successful move. Log the delete failure
            // separately so the user has a hint about cleaning up.
            try { File.Delete(zipPath); }
            catch (Exception delEx)
            {
                GD.PrintErr($"[ModFramework] ModZipDropInstaller: extracted ok but couldn't delete source zip {zipPath}: {delEx.GetType().Name}: {delEx.Message} — remove it manually");
            }

            GD.Print($"[ModFramework] ModZipDropInstaller: extracted {Path.GetFileName(zipPath)} -> {destPath}/");
            return ExtractResult.Extracted;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModZipDropInstaller: failed to extract {zipPath}: {ex.GetType().Name}: {ex.Message}");
            CleanupOrphanTempFolders(parent, folderName);
            return ExtractResult.Failed;
        }
    }

    // Best-effort: walk the parent directory for any leftover temp folders
    // from a failed extract and remove them. Original zip stays in place for
    // a future retry. Swallows all exceptions because this is a defensive
    // cleanup pass — if it can't run, the leftover folder is mildly ugly
    // but harmless (won't conflict with the next attempt since the guid
    // suffix is fresh each time).
    private static void CleanupOrphanTempFolders(string? parent, string? folderName)
    {
        if (parent == null || folderName == null) return;
        try
        {
            foreach (var leftover in Directory.EnumerateDirectories(parent, $".pmfw-zipdrop-{folderName}-*"))
                Directory.Delete(leftover, recursive: true);
        }
        catch (Exception)
        {
            // intentional swallow: defensive cleanup, not load-bearing
        }
    }
}
