using Godot;

namespace PratfallModFramework;

public static class ManifestManager
{
    public static List<ModManifest> ScanLocalMods()
    {
        var allMods = new Dictionary<string, ModManifest>();

        // Path 1: user://mods/ — manually installed by player
        ScanDirectory("user://mods/", allMods);

        // Path 2: game install directory's mods/ folder (next to Pratfall.exe)
        var gameDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath());
        if (gameDir != null)
        {
            var gameModsDir = System.IO.Path.Combine(gameDir, "mods");
            if (System.IO.Directory.Exists(gameModsDir))
                ScanOsDirectory(gameModsDir, allMods);
        }

        return allMods.Values.ToList();
    }

    private static void ScanOsDirectory(string dirPath, Dictionary<string, ModManifest> allMods)
    {
        foreach (var subDir in System.IO.Directory.GetDirectories(dirPath))
        {
            var manifestPath = System.IO.Path.Combine(subDir, "manifest.json");
            if (!System.IO.File.Exists(manifestPath)) continue;

            try
            {
                var json = System.IO.File.ReadAllText(manifestPath);
                var manifest = ModManifest.FromJson(
                    json,
                    directoryName: System.IO.Path.GetFileName(subDir),
                    directoryPath: subDir);
                if (!allMods.ContainsKey(manifest.Id))
                    allMods[manifest.Id] = manifest;
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[ModFramework] Failed to parse manifest at {manifestPath}: {e.Message}");
            }
        }
    }

    private static void ScanDirectory(string basePath, Dictionary<string, ModManifest> allMods)
    {
        var dir = DirAccess.Open(basePath);
        if (dir == null)
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(basePath));
            return;
        }
        dir.ListDirBegin();
        while (true)
        {
            var entry = dir.GetNext();
            if (string.IsNullOrEmpty(entry)) break;
            if (entry == "." || entry == "..") continue;
            if (!dir.CurrentIsDir()) continue;

            var manifestPath = $"{basePath}{entry}/manifest.json";
            if (!global::Godot.FileAccess.FileExists(manifestPath)) continue;

            try
            {
                var json = global::Godot.FileAccess.GetFileAsString(manifestPath);
                var manifest = ModManifest.FromJson(
                    json,
                    directoryName: entry,
                    directoryPath: ProjectSettings.GlobalizePath($"{basePath}{entry}"));
                // Prefer user:// version if same mod exists in res://
                if (!allMods.ContainsKey(manifest.Id) || basePath.StartsWith("user://", StringComparison.Ordinal))
                    allMods[manifest.Id] = manifest;
            }
            catch (System.Exception e)
            {
                GD.PrintErr($"[ModFramework] Failed to parse manifest in {manifestPath}: {e.Message}");
            }
        }
        dir.ListDirEnd();
    }

    public static List<string> GetModIds(List<ModManifest> manifests)
    {
        return manifests.Select(m => m.Id).ToList();
    }

    public static List<string> DetectMissingMods(List<string> localIds, List<string> peerIds)
    {
        return peerIds.Except(localIds).ToList();
    }
}
