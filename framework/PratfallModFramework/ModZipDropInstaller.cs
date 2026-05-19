using System.IO.Compression;
using Godot;

namespace PratfallModFramework;

// Zip-drop install — auto-extract any `.zip` files dropped into our mod-scan
// roots (Thunderstore downloads, manual zip drops, GitHub release artifacts).
// Pratfall's loader doesn't handle archives; without this, a player dropping
// `MyMod.zip` into `%APPDATA%\Pratfall\mods\` would see nothing happen.
//
// Behavior per zip:
//   1. Validate the source path is inside one of our trusted scan roots
//      (defense against being pointed at arbitrary directories).
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
        foreach (var root in EnumerateScanRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var zipPath in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
                {
                    TryExtractOne(zipPath);
                }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] ModZipDropInstaller: enumerate failed for {root}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static IEnumerable<string> EnumerateScanRoots()
    {
        // Mirrors ManifestManager.ScanLocalMods scan roots minus the Steam
        // Workshop content folder (Steam owns those folders; we should never
        // write into them, and Workshop downloads aren't .zip-format).

        var profilePath = FrameworkProfile.ProfileModsDirectory;
        if (!string.IsNullOrEmpty(profilePath))
            yield return profilePath;

        var userModsAbs = ProjectSettings.GlobalizePath("user://mods/");
        if (!string.IsNullOrWhiteSpace(userModsAbs))
            yield return userModsAbs;

        if (!FrameworkProfile.IsActive)
        {
            var gameDir = Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(gameDir))
            {
                var gameModsDir = Path.Combine(gameDir, "mods");
                yield return gameModsDir;
            }
        }
    }

    private static void TryExtractOne(string zipPath)
    {
        string? parent = null;
        string? folderName = null;
        string? destPath = null;
        try
        {
            parent = Path.GetDirectoryName(zipPath);
            folderName = Path.GetFileNameWithoutExtension(zipPath);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(folderName))
            {
                GD.PrintErr($"[ModFramework] ModZipDropInstaller: bad zip path {zipPath}");
                return;
            }

            destPath = Path.Combine(parent, folderName);
            if (Directory.Exists(destPath))
            {
                // Don't overwrite an existing install — could be a different
                // version, could be the same mod with user customization. Log
                // and leave both the zip and the existing folder alone; user
                // can resolve manually if they intended an update.
                GD.Print($"[ModFramework] ModZipDropInstaller: target folder already exists, skipping ({destPath}). Remove the folder if you want the zip extracted.");
                return;
            }

            // Extract to a temp folder next to the destination, then rename.
            // Means a partial / failed extraction never leaves a broken mod
            // folder in place. Temp folder uses a guid to avoid collisions.
            var tempDest = Path.Combine(parent, $".pmfw-zipdrop-{folderName}-{Guid.NewGuid():N}");
            ZipFile.ExtractToDirectory(zipPath, tempDest); // throws on zip-slip in .NET 7+
            Directory.Move(tempDest, destPath);

            File.Delete(zipPath);
            GD.Print($"[ModFramework] ModZipDropInstaller: extracted {Path.GetFileName(zipPath)} -> {destPath}/");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModZipDropInstaller: failed to extract {zipPath}: {ex.GetType().Name}: {ex.Message}");
            // Best-effort cleanup of the temp dest if rename didn't happen.
            // Original zip stays in place for a future retry.
            try
            {
                if (parent != null && folderName != null)
                {
                    foreach (var leftover in Directory.EnumerateDirectories(parent, $".pmfw-zipdrop-{folderName}-*"))
                        Directory.Delete(leftover, recursive: true);
                }
            }
            catch { /* cleanup best-effort */ }
        }
    }
}
