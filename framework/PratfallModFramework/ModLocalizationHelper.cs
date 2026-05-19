using System.Text;
using System.Text.Json;
using Godot;
using HarmonyLib;

namespace PratfallModFramework;

// Helper for mods that want to add a new in-game language. Pratfall's loader has
// native support for user-installed locales via LocalizationManager.LoadUserLocalizations,
// which scans `<userData>/localization/` for files matching `_*.json` and adds them
// to AvailableLocales (the same list the in-game language selector reads). This
// helper writes the mod's JSON to the right folder with the right naming convention
// and triggers the rescan.
//
// Per re-audit of Pratfall.dll (2026-05-17):
//   - User-locale folder: `<Game.Platform.GetUserDataPath()>/localization`
//   - File-name filter: must end with `.json` AND MUST NOT start with `_`
//     (`LoadJsonFiles`: EndsWith(".json") AND NOT StartsWith("_")). Leading
//     `_` is reserved by Pratfall — probably for templates/disabled files.
//   - Registered locale ID: `"zuser" + filename-without-extension`. So a file
//     `MyMod_es.json` registers as locale ID `"zuserMyMod_es"`. This namespaces
//     user locales away from system locales ("en", "de", etc.) so they can't
//     collide. The in-game selector's display name is the filename basename.
//   - Use `ComputeRegisteredLocaleId(modId, localeCode)` to get the string a
//     mod would pass to `LocalizationManager.IsLocaleAvailable` or to
//     `TranslationServer.SetLocale`.
//   - Loader gates on `GameConfig.AllowUserLocalization` — if the game build
//     has this disabled, LoadUserLocalizations is a no-op.
//
// Typical mod usage:
//
//   public static class MyLocalizationMod
//   {
//       private static IDisposable? _registration;
//       public static void OnLoad()
//       {
//           var translations = new Dictionary<string, string>
//           {
//               { "MAIN_MENU_PLAY", "Jugar" },
//               { "MAIN_MENU_OPTIONS", "Opciones" },
//               // ...
//           };
//           _registration = ModLocalizationHelper.Register(
//               modId: "MyLocalizationMod",
//               localeCode: "es_419",
//               translations: translations);
//       }
//       public static void OnUnload() => _registration?.Dispose();
//   }
public static class ModLocalizationHelper
{
    // ------------------------------------------------------------
    //  ForceEnableUserLocales — opt-in unlock for user-locale loading
    // ------------------------------------------------------------
    // On shipped Pratfall builds (verified `1.1.0.R2973`, 2026-05-18) the
    // game gates user-locale loading behind `Game.Config.AllowUserLocalization`,
    // which `GameEntry._Ready` explicitly sets to false at startup (Cecil
    // instructions: ldc.i4.0 + call set_AllowUserLocalization). So even though
    // LoadUserLocalizations exists and the file-naming convention is documented,
    // user locale .json files are silently skipped on the public release.
    //
    // Calling ForceEnableUserLocales() installs a Harmony prefix on the
    // GameConfig.AllowUserLocalization getter that always returns true,
    // unlocking the loader. Use this if your mod ships new-language locale
    // files and you've accepted the trade-off: Tim hasn't blessed the user-
    // locale UX yet (file format / locale-name collision rules / mod-override
    // priority are all undecided), so the experience may shift when he flips
    // the flag officially.
    //
    // Idempotent — calling more than once is a no-op. The patch persists for
    // the process lifetime; there is intentionally no uninstall (mods that
    // call this take responsibility for the lobby-wide consequences of new
    // locales appearing in the language picker).
    //
    // Today (R2973) the workaround for "patch existing locale" cases is to
    // call TranslationServer.AddTranslation directly; that works without this
    // patch. ForceEnableUserLocales is for the "add a brand-new selectable
    // language" case, which TranslationServer alone CAN'T solve (the picker
    // reads from LocalizationManager.AvailableLocales, populated only when the
    // gated JSON-file path runs).
    public static void ForceEnableUserLocales()
    {
        if (_forceEnableApplied) return;

        try
        {
            var getter = AccessTools.PropertyGetter(typeof(global::GameConfig), "AllowUserLocalization");
            if (getter == null)
            {
                GD.PrintErr("[ModFramework] ModLocalizationHelper.ForceEnableUserLocales: GameConfig.AllowUserLocalization getter not found — Pratfall version may have renamed the property.");
                return;
            }

            var harmony = new Harmony("PratfallModFramework.ForceEnableUserLocales");
            harmony.Patch(getter, prefix: new HarmonyMethod(typeof(ModLocalizationHelper), nameof(AllowUserLocalizationPrefix)));
            _forceEnableApplied = true;
            GD.Print("[ModFramework] ModLocalizationHelper: ForceEnableUserLocales is ON — Game.Config.AllowUserLocalization now reports true regardless of GameEntry._Ready's setting. User-installed locales will be loaded.");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModLocalizationHelper.ForceEnableUserLocales failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool IsForceEnableUserLocalesActive => _forceEnableApplied;

    private static bool _forceEnableApplied;

    private static bool AllowUserLocalizationPrefix(ref bool __result)
    {
        __result = true;
        return false; // skip original
    }

    // ------------------------------------------------------------

    // Registers a locale by writing `_<modId>_<localeCode>.json` into the game's
    // user-locale folder and calling LoadUserLocalizations to refresh AvailableLocales.
    // The mod's JSON shape is "key": "translated value" — same as Pratfall's own.
    public static IDisposable Register(string modId, string localeCode, IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));
        if (string.IsNullOrWhiteSpace(localeCode))
            throw new ArgumentException("localeCode is required", nameof(localeCode));
        ArgumentNullException.ThrowIfNull(translations);

        var json = JsonSerializer.Serialize(translations, s_indentedJsonOptions);
        return RegisterRaw(modId, localeCode, json);
    }

    // Cached per CA1869 — JsonSerializerOptions is expensive to construct and
    // safe to share across threads when not mutated after construction.
    private static readonly JsonSerializerOptions s_indentedJsonOptions = new() { WriteIndented = true };

    // Same as Register, but takes the JSON string the mod already built. Useful when
    // the mod prefers to author its locale data in a known schema (e.g. shipping a
    // .json next to its DLL) and just wants the file moved into the right folder.
    public static IDisposable RegisterRaw(string modId, string localeCode, string jsonContent)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));
        if (string.IsNullOrWhiteSpace(localeCode))
            throw new ArgumentException("localeCode is required", nameof(localeCode));
        ArgumentNullException.ThrowIfNull(jsonContent);

        var dir = GetUserLocaleFolder();
        if (dir == null)
        {
            GD.PrintErr($"[ModFramework] ModLocalizationHelper: could not resolve user locale folder for {modId}/{localeCode}");
            return new NullRegistration();
        }
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Loader requires files to end with `.json` and NOT start with `_`
        // (leading-underscore files are reserved/skipped). Mod id + locale code
        // together keep two mods from colliding on the same filename.
        var fileName = $"{Sanitize(modId)}_{Sanitize(localeCode)}.json";
        var path = Path.Combine(dir, fileName);

        try
        {
            File.WriteAllText(path, jsonContent, Encoding.UTF8);
            GD.Print($"[ModFramework] Wrote locale file {fileName} ({jsonContent.Length} bytes)");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ModLocalizationHelper: failed to write {path}: {ex.Message}");
            return new NullRegistration();
        }

        TriggerRescan();
        return new LocaleRegistration(path, modId, localeCode);
    }

    private static void TriggerRescan()
    {
        try
        {
            var mgr = global::LocalizationManager.Instance;
            if (mgr == null)
            {
                GD.Print("[ModFramework] LocalizationManager.Instance is null — locale will load on next game start");
                return;
            }

            // Pre-check the gates LoadUserLocalizations enforces — if either is
            // false the call is a no-op and the mod author won't see their locale
            // even with a correctly-named file. Surface a clear warning instead
            // of leaving them to wonder why nothing happens.
            try
            {
                if (!global::Game.Config.AllowUserLocalization)
                {
                    GD.PrintErr("[ModFramework] LocalizationManager.LoadUserLocalizations is gated by Game.Config.AllowUserLocalization=false on this Pratfall build. The user-locale file was written, but the game won't load it. Wait for Pratfall to enable AllowUserLocalization, or load translations via TranslationServer.AddTranslation directly.");
                    return;
                }
                if (global::Game.Platform != null && !global::Game.Platform.IsSupportingDirectFileAccess())
                {
                    GD.PrintErr("[ModFramework] LocalizationManager.LoadUserLocalizations is gated by Game.Platform.IsSupportingDirectFileAccess()=false on this platform (likely console / EOS-only). User locales won't load.");
                    return;
                }
            }
            catch { /* gate-introspection failure shouldn't block the legitimate call */ }

            mgr.LoadUserLocalizations();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] LoadUserLocalizations failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? GetUserLocaleFolder()
    {
        // Match LocalizationManager.CreateUserDataDirectory: <platform user data>/localization
        //
        // Game.Platform.GetUserDataPath() returns a Godot `user://...` URI on Steam
        // (the game's own code uses Godot.DirAccess which understands the URI). We
        // write with System.IO, so we need to ProjectSettings.GlobalizePath it to
        // get a real filesystem path first.
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var userData = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(userData)) return null;
            var globalized = ProjectSettings.GlobalizePath(userData);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = userData;
            return Path.Combine(globalized, "localization");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Could not resolve platform user data path: {ex.Message}");
            return null;
        }
    }

    // Returns the locale ID that Pratfall actually registers for a given
    // (modId, localeCode) pair. Use this when calling
    // `LocalizationManager.IsLocaleAvailable(...)` or
    // `TranslationServer.SetLocale(...)` to refer back to your mod's locale.
    public static string ComputeRegisteredLocaleId(string modId, string localeCode)
        => $"zuser{Sanitize(modId)}_{Sanitize(localeCode)}";

    private static string Sanitize(string s)
    {
        // Filesystem-safe + matches the leading-underscore convention. Strip anything
        // that could break path parsing or cross folders.
        var clean = new StringBuilder();
        foreach (var ch in s)
            clean.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
        return clean.ToString();
    }

    private sealed class LocaleRegistration : IDisposable
    {
        private string? _path;
        private readonly string _modId;
        private readonly string _localeCode;

        public LocaleRegistration(string path, string modId, string localeCode)
        {
            _path = path;
            _modId = modId;
            _localeCode = localeCode;
        }

        public void Dispose()
        {
            if (_path == null) return;
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
                GD.Print($"[ModFramework] Removed locale file for {_modId}/{_localeCode}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] Failed to delete locale file {_path}: {ex.Message}");
            }
            _path = null;

            // Rescan so AvailableLocales drops the entry too.
            try { global::LocalizationManager.Instance?.LoadUserLocalizations(); }
            catch { /* best-effort cleanup */ }
        }
    }

    private sealed class NullRegistration : IDisposable { public void Dispose() { } }
}
