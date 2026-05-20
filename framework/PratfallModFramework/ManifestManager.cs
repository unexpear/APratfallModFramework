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
        // Tracks normalized absolute scan paths so a misconfigured
        // --qh-mod-directory that happens to equal user://mods or
        // <game>/mods doesn't get walked twice (mod IDs would still
        // de-dupe via allMods, but the directory iteration + manifest
        // parsing is wasted work + produces confusing log lines).
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Path 0: extract any .zip files dropped into our scan roots before
        // discovery runs (Thunderstore-style + manual zip drops).
        ModZipDropInstaller.ExtractAll();

        // Path 1: --qh-mod-directory profile path (r2modman / Thunderstore).
        // Scanned FIRST so profile-installed mods take precedence over any
        // collisions with default locations — matches the user's expectation
        // that "this profile's version of mod X is the one that loads".
        var profilePath = FrameworkProfile.ProfileModsDirectory;
        if (!string.IsNullOrEmpty(profilePath) && System.IO.Directory.Exists(profilePath)
            && TryMarkScanned(profilePath, scannedPaths))
        {
            ScanOsDirectory(profilePath, allMods);
        }

        // Path 2: user://mods/ — manually installed by player
        var userModsAbs = ProjectSettings.GlobalizePath("user://mods/");
        if (!string.IsNullOrWhiteSpace(userModsAbs) && TryMarkScanned(userModsAbs, scannedPaths))
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
                if (System.IO.Directory.Exists(gameModsDir) && TryMarkScanned(gameModsDir, scannedPaths))
                    ScanOsDirectory(gameModsDir, allMods);
            }
        }

        // Path 4: Steam Workshop content — discovered Workshop-subscribed mods.
        // Replaces what Pratfall's native ModManager used to do for us (which
        // we've turned off as of 2026-05-18 — see OfficialModBridge.cs).
        // Always scanned, even under a profile — Workshop subscriptions are
        // global to the Steam account, not per-profile. ScanWorkshopMods has
        // its own per-library-folder loop and skips Steam-managed paths the
        // user shouldn't be tagging as a profile root, so no dedup needed.
        ScanWorkshopMods(allMods);

        return allMods.Values.ToList();
    }

    // Normalize an absolute path (full path + trailing separator stripped) and
    // record it. Returns false if we've already scanned an equivalent path —
    // call sites should skip in that case.
    private static bool TryMarkScanned(string path, HashSet<string> seen)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path).TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            return seen.Add(full);
        }
        catch
        {
            // If normalization fails (bad path chars, IO error), be permissive
            // and allow the scan — worst case is a duplicate walk.
            return true;
        }
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
                var manifest = TryParseModManifestFromOsPath(subDir);
                if (manifest == null) continue;

                // Tag as Workshop. Workshop folders are named after the
                // published-file ID — parse to ulong for the WorkshopId field
                // (silently leave 0 if folder name isn't numeric, which
                // shouldn't happen for real Steam Workshop downloads).
                manifest.IsSteamWorkshopMod = true;
                if (ulong.TryParse(manifest.DirectoryName, out var wsid))
                    manifest.WorkshopId = wsid;

                // First Workshop entry wins for a given mod ID; locally-
                // installed entries take precedence (added in earlier passes).
                if (!allMods.ContainsKey(manifest.Id))
                    allMods[manifest.Id] = manifest;
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
                    var match = rx.Match(line);
                    if (match.Success)
                    {
                        // VDF escapes backslashes as \\ — unescape to a real path.
                        var libPath = match.Groups[1].Value.Replace("\\\\", "\\");
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
            var manifest = TryParseModManifestFromOsPath(subDir);
            if (manifest == null) continue;
            if (!allMods.ContainsKey(manifest.Id))
                allMods[manifest.Id] = manifest;
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

            var manifest = TryParseModManifestFromGodotPath(basePath, entry);
            if (manifest == null) continue;

            // Prefer user:// version if same mod exists elsewhere; otherwise
            // first-wins. (basePath is the only "do we override?" signal; the
            // sole caller passes "user://mods/".)
            if (!allMods.ContainsKey(manifest.Id) || basePath.StartsWith("user://", StringComparison.Ordinal))
                allMods[manifest.Id] = manifest;
        }
        dir.ListDirEnd();
    }

    // Shared core: parse manifest.json under an OS-absolute subdir.
    // Returns null + logs on missing-file or parse-error. DirectoryName +
    // DirectoryPath are set on the manifest; post-parse tagging
    // (IsSteamWorkshopMod etc.) is the caller's responsibility.
    private static ModManifest? TryParseModManifestFromOsPath(string subDir)
    {
        var manifestPath = System.IO.Path.Combine(subDir, "manifest.json");
        if (!System.IO.File.Exists(manifestPath)) return null;
        try
        {
            var json = System.IO.File.ReadAllText(manifestPath);
            return ModManifest.FromJson(
                json,
                directoryName: System.IO.Path.GetFileName(subDir),
                directoryPath: subDir);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to parse manifest at {manifestPath}: {ex.Message}");
            return null;
        }
    }

    // Shared core: parse manifest.json under a Godot-virtual subdir (user:// or res://).
    // Mirrors TryParseModManifestFromOsPath but uses Godot.FileAccess for the
    // read so it works on packed res:// paths in addition to the OS filesystem.
    private static ModManifest? TryParseModManifestFromGodotPath(string basePath, string entry)
    {
        var manifestPath = $"{basePath}{entry}/manifest.json";
        if (!global::Godot.FileAccess.FileExists(manifestPath)) return null;
        try
        {
            var json = global::Godot.FileAccess.GetFileAsString(manifestPath);
            return ModManifest.FromJson(
                json,
                directoryName: entry,
                directoryPath: ProjectSettings.GlobalizePath($"{basePath}{entry}"));
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to parse manifest in {manifestPath}: {ex.Message}");
            return null;
        }
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
