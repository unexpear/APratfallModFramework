using Godot;

namespace PratfallModFramework;

public static class ManifestManager
{
    // Pratfall's Steam app ID — used to locate workshop content under
    // <steam>/steamapps/workshop/content/<appid>/<workshopid>/.
    private const string PratfallSteamAppId = "4244510";

    public static List<ModManifest> ScanLocalMods()
    {
        var allMods = new Dictionary<string, ModManifest>();

        // Path 0: extract any .zip files dropped into our scan roots before
        // discovery runs (Thunderstore-style + manual zip drops).
        ModZipDropInstaller.ExtractAll();

        // Path 1: --qh-mod-directory profile path (r2modman / Thunderstore).
        // Scanned FIRST so profile-installed mods take precedence over any
        // collisions with default locations — matches the user's expectation
        // that "this profile's version of mod X is the one that loads".
        var profilePath = FrameworkProfile.ProfileModsDirectory;
        if (!string.IsNullOrEmpty(profilePath) && System.IO.Directory.Exists(profilePath))
            ScanOsDirectory(profilePath, allMods);

        // Path 2: user://mods/ — manually installed by player
        ScanDirectory("user://mods/", allMods);

        // Path 3: game install directory's mods/ folder (next to Pratfall.exe).
        // Skipped when a profile is active — r2modman manages its own folder,
        // and double-scanning the default path would defeat profile isolation.
        if (!FrameworkProfile.IsActive)
        {
            var gameDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath());
            if (gameDir != null)
            {
                var gameModsDir = System.IO.Path.Combine(gameDir, "mods");
                if (System.IO.Directory.Exists(gameModsDir))
                    ScanOsDirectory(gameModsDir, allMods);
            }
        }

        // Path 4: Steam Workshop content — discovered Workshop-subscribed mods.
        // Replaces what Pratfall's native ModManager used to do for us (which
        // we've turned off as of 2026-05-18 — see OfficialModBridge.cs).
        // Always scanned, even under a profile — Workshop subscriptions are
        // global to the Steam account, not per-profile.
        ScanWorkshopMods(allMods);

        return allMods.Values.ToList();
    }

    // Locate every Steam library folder, look for
    // steamapps/workshop/content/4244510/<workshopid>/manifest.json, and treat
    // each Workshop subscription as a mod. Marks manifests with
    // IsSteamWorkshopMod=true and WorkshopId=<folder-name> so the rest of the
    // framework (settings UI, mod inspector, crash reports) can distinguish
    // Workshop from local mods.
    private static void ScanWorkshopMods(Dictionary<string, ModManifest> allMods)
    {
        foreach (var libraryFolder in EnumerateSteamLibraryFolders())
        {
            var workshopRoot = System.IO.Path.Combine(libraryFolder, "steamapps", "workshop", "content", PratfallSteamAppId);
            if (!System.IO.Directory.Exists(workshopRoot)) continue;

            foreach (var subDir in System.IO.Directory.GetDirectories(workshopRoot))
            {
                var manifestPath = System.IO.Path.Combine(subDir, "manifest.json");
                if (!System.IO.File.Exists(manifestPath)) continue;

                try
                {
                    var json = System.IO.File.ReadAllText(manifestPath);
                    var folderName = System.IO.Path.GetFileName(subDir);
                    var manifest = ModManifest.FromJson(
                        json,
                        directoryName: folderName,
                        directoryPath: subDir);

                    // Tag as Workshop. Workshop folders are named after the
                    // published-file ID — parse to ulong for the WorkshopId
                    // field (silently leave 0 if folder name isn't numeric, which
                    // shouldn't happen for real Steam Workshop downloads).
                    manifest.IsSteamWorkshopMod = true;
                    if (ulong.TryParse(folderName, out var wsid))
                        manifest.WorkshopId = wsid;

                    // First Workshop entry wins for a given mod ID; locally-
                    // installed entries take precedence (they were added in
                    // earlier passes).
                    if (!allMods.ContainsKey(manifest.Id))
                        allMods[manifest.Id] = manifest;
                }
                catch (System.Exception e)
                {
                    GD.PrintErr($"[ModFramework] Failed to parse Workshop manifest at {manifestPath}: {e.Message}");
                }
            }
        }
    }

    // Find every Steam library folder on disk by reading
    // <steam>/steamapps/libraryfolders.vdf. Returns absolute paths to the
    // library roots (the folders that *contain* steamapps/, not steamapps/
    // itself). Falls back to the canonical Program Files (x86) Steam path if
    // the registry lookup fails — covers single-library default installs.
    private static IEnumerable<string> EnumerateSteamLibraryFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? steamPath = null;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
                steamPath = key?.GetValue("SteamPath") as string;
            }
            catch { /* registry unavailable — fall through */ }
        }

        if (!string.IsNullOrEmpty(steamPath) && seen.Add(steamPath))
            yield return steamPath;

        // Read libraryfolders.vdf to find non-default library locations
        // (custom drives, e.g. D:\SteamLibrary).
        var vdfPath = string.IsNullOrEmpty(steamPath)
            ? null
            : System.IO.Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (vdfPath != null && System.IO.File.Exists(vdfPath))
        {
            string[]? lines = null;
            try { lines = System.IO.File.ReadAllLines(vdfPath); }
            catch (System.Exception e) { GD.PrintErr($"[ModFramework] Failed to read {vdfPath}: {e.Message}"); }

            if (lines != null)
            {
                // libraryfolders.vdf has a simple key/value structure; library paths
                // appear on lines like:   "path"   "D:\\SteamLibrary"
                var rx = new System.Text.RegularExpressions.Regex("\"path\"\\s*\"([^\"]+)\"");
                foreach (var line in lines)
                {
                    var m = rx.Match(line);
                    if (m.Success)
                    {
                        // VDF escapes backslashes as \\ — unescape to a real path.
                        var libPath = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (System.IO.Directory.Exists(libPath) && seen.Add(libPath))
                            yield return libPath;
                    }
                }
            }
        }
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
