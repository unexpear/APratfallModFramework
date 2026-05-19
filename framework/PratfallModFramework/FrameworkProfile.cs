using Godot;

namespace PratfallModFramework;

// Detects Pratfall's `--qh-mod-directory <path>` CLI flag (Tim's launch-arg
// for profile-based mod managers like r2modman / Thunderstore). When set,
// the framework treats that path as both:
//
//   1. An additional mod-scan root (mods r2modman drops there appear in our
//      Mods dialog alongside whatever's in user://mods/, <game>/mods/, and
//      Steam Workshop content).
//   2. The location for OUR state file (modframework-state.json — enabled mods,
//      approved fingerprints), so each r2modman profile gets independent
//      framework state. Without this, switching profiles would share enabled-
//      mod selections across profiles (the footgun option-A would have).
//
// When the flag isn't set (the default for normal Steam launches), all paths
// resolve to their original locations and nothing changes for single-install
// users.
//
// Reads OS.GetCmdlineArgs() directly rather than depending on Pratfall's
// ModManager.ModsDirectory because:
//   - It works regardless of when in Pratfall's lifecycle we initialize.
//   - We've turned off native ModManager.Setup() so its ModsDirectory may
//     not be populated when we look.
//   - SystemArguments is Pratfall-internal; using OS.GetCmdlineArgs keeps
//     us out of game-private API.
//
// Cached on first access — CLI args don't change at runtime, so re-parsing
// every call is wasteful.
internal static class FrameworkProfile
{
    private const string FlagName = "--qh-mod-directory";

    private static string? _profileModsDirectory;
    private static bool _resolved;

    // The path passed to --qh-mod-directory, or null if the flag wasn't set
    // or its value was empty/whitespace. Absolute path; not Godot-virtual.
    public static string? ProfileModsDirectory
    {
        get
        {
            if (!_resolved)
            {
                _profileModsDirectory = ParseFlag();
                _resolved = true;
                if (_profileModsDirectory != null)
                    GD.Print($"[ModFramework] FrameworkProfile: --qh-mod-directory detected -> {_profileModsDirectory} (treated as additional scan root + state-file location)");
            }
            return _profileModsDirectory;
        }
    }

    // True iff a profile path is in effect. Convenience for `if (FrameworkProfile.IsActive) { ... }`
    // call sites that don't need the actual string.
    public static bool IsActive => ProfileModsDirectory != null;

    private static string? ParseFlag()
    {
        try
        {
            var args = OS.GetCmdlineArgs();
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                // Two accepted forms:
                //   --qh-mod-directory <path>     (space-separated)
                //   --qh-mod-directory=<path>     (equals-separated; common in shell scripting)
                if (args[i] == FlagName && i + 1 < args.Length)
                {
                    var v = args[i + 1].Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }
                const string eqPrefix = FlagName + "=";
                if (args[i].StartsWith(eqPrefix, StringComparison.Ordinal))
                {
                    var v = args[i].Substring(eqPrefix.Length).Trim();
                    return string.IsNullOrEmpty(v) ? null : v;
                }
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] FrameworkProfile.ParseFlag failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    // Resolves the state-file path: profile-local when --qh-mod-directory is
    // set, otherwise the Godot user:// path. Returned path is absolute.
    public static string ResolveStateFilePath()
    {
        var profile = ProfileModsDirectory;
        if (profile != null)
            return System.IO.Path.Combine(profile, "modframework-state.json");
        return ProjectSettings.GlobalizePath("user://modframework-state.json");
    }
}
