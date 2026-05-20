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
            && PathUtil.TryAddNormalized(scannedPaths, profilePath))
        {
            ScanOsDirectory(profilePath, allMods);
        }

        // Path 2: user://mods/ — manually installed by player. Globalize to an
        // OS-absolute path so we can use the same scanner as the profile path.
        // CreateDirectory is idempotent — creates the folder on first launch so
        // zip-drops have somewhere to land and the dialog works without forcing
        // the player to mkdir manually.
        var userModsAbs = ProjectSettings.GlobalizePath("user://mods/");
        if (!string.IsNullOrEmpty(userModsAbs) && PathUtil.TryAddNormalized(scannedPaths, userModsAbs))
        {
            try { System.IO.Directory.CreateDirectory(userModsAbs); }
            catch (System.Exception ex) { GD.PrintErr($"[ModFramework] Failed to ensure user mods folder {userModsAbs}: {ex.Message}"); }
            if (System.IO.Directory.Exists(userModsAbs))
                ScanOsDirectory(userModsAbs, allMods);
        }

        // Path 3: Pratfall install folder, recursively. A subfolder is treated as
        // a mod if it contains manifest.json; we don't recurse further once found
        // (a mod's internal folders aren't sub-mods). Subfolders WITHOUT a manifest
        // are walked so mods can live at any path — <game>/MyMod/, <game>/community/MyMod/,
        // <game>/mods/MyMod/, etc. Hard-excludes the game runtime folder
        // (data_Pratfall_*) which has hundreds of native + .NET files but never a
        // legitimate mod. Bounded depth 5 — defensive against pathological nesting.
        //
        // Skipped when a profile is active — r2modman manages its own folder, and
        // walking the install root would leak the user's standalone-install mods
        // into the profile.
        //
        // Stays inside <game>/ — never escapes the install root. Other roots
        // (user://mods/, Workshop content) are scanned by their own dedicated
        // paths above/below.
        if (!FrameworkProfile.IsActive)
        {
            var gameDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath());
            if (!string.IsNullOrEmpty(gameDir) && System.IO.Directory.Exists(gameDir)
                && PathUtil.TryAddNormalized(scannedPaths, gameDir))
            {
                ScanInstallRootRecursive(gameDir, allMods);
            }
        }

        // Path 4: Steam Workshop content — discovered Workshop-subscribed mods.
        // Replaces what Pratfall's native ModManager used to do for us (which
        // we've turned off as of 2026-05-18 — see OfficialModBridge.cs).
        // Always scanned, even under a profile — Workshop subscriptions are
        // global to the Steam account, not per-profile. No dedup against
        // scannedPaths: ScanWorkshopMods walks Steam library roots directly to
        // steamapps/workshop/content/<appid>/, which never overlaps the
        // profile / user / install roots Paths 1-3 cover.
        ScanWorkshopMods(allMods);

        // Collision precedence note: first-listed-wins for a given mod ID. Under
        // the recursive install-root scan (Path 3), "first" within that path is
        // BFS-order — alphabetically-earliest sibling at the shallowest depth.
        // Across paths: Path 1 beats 2 beats 3 beats 4. So if MyMod exists at
        // BOTH <game>/community/MyMod/ AND <game>/mods/MyMod/, the alphabetic-
        // earlier `community/` wins. Predictable but not obvious — moving or
        // renaming a folder can flip which version loads.

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

    // Shallow scan: parse every immediate subdir under dirPath as a candidate
    // mod folder. Used for r2modman profile + user://mods/ — flat layouts
    // where mods are direct children of the root.
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

    // Recursive scan from the Pratfall install root. A folder with manifest.json
    // is treated as a mod (parsed + added, no further descent into its
    // internals). Folders without are walked up to InstallRootScanMaxDepth
    // (5 levels) so mods can live at flexible paths — <game>/MyMod/,
    // <game>/community/MyMod/, <game>/mods/MyMod/, etc. Hard-excludes the game
    // runtime folder (data_Pratfall_*) which has hundreds of native + .NET
    // files but never a legitimate mod. Iterative BFS — no stack blowup on
    // deep trees + the depth cap also protects against symlink loops.
    private const int InstallRootScanMaxDepth = 5;

    private static void ScanInstallRootRecursive(string root, Dictionary<string, ModManifest> allMods)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            // The install root itself isn't a mod (Pratfall.exe lives there);
            // skip the manifest check for it and just walk children. String.Equals
            // (Ordinal) rather than ReferenceEquals — works the same today since
            // we only enqueue `root` once, but stays correct if a future refactor
            // ever re-creates the root string from path components.
            if (!string.Equals(current, root, StringComparison.Ordinal))
            {
                var manifest = TryParseModManifestFromOsPath(current);
                if (manifest != null)
                {
                    if (!allMods.ContainsKey(manifest.Id))
                        allMods[manifest.Id] = manifest;
                    continue; // found a mod; don't recurse into its internals
                }
            }

            if (depth >= InstallRootScanMaxDepth) continue;

            try
            {
                foreach (var child in System.IO.Directory.EnumerateDirectories(current))
                {
                    if (IsExcludedInstallSubfolder(System.IO.Path.GetFileName(child))) continue;
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (System.Exception ex)
            {
                // Permission denied, transient IO error, etc. Log + continue —
                // a deeper folder we couldn't read shouldn't kill the whole scan.
                GD.PrintErr($"[ModFramework] Skipping unreadable dir {current}: {ex.Message}");
            }
        }
    }

    private static bool IsExcludedInstallSubfolder(string folderName)
    {
        // Pratfall ships its Godot/.NET runtime + native DLLs (and OUR framework
        // DLLs) under data_Pratfall_<platform>_<arch>/. Prefix-match so future
        // Pratfall platform variants (linux_x86_64, macos_arm64, etc.) are
        // covered without needing a new release of the framework just to know
        // about a new exclude.
        return folderName.StartsWith("data_Pratfall_", StringComparison.OrdinalIgnoreCase);
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

}
