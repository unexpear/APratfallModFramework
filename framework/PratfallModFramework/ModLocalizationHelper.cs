using System.Text;
using System.Text.Json;
using Godot;

namespace PratfallModFramework;

// Helper for mods that want to add a new in-game language. Pratfall's loader has
// native support for user-installed locales via LocalizationManager.LoadUserLocalizations,
// which scans `<userData>/localization/` for files matching `_*.json` and adds them
// to AvailableLocales (the same list the in-game language selector reads). This
// helper writes the mod's JSON to the right folder with the right naming convention
// and triggers the rescan.
//
// Per audit of Pratfall.dll (2026-05-16):
//   - User-locale folder: `<Game.Platform.GetUserDataPath()>/localization`
//   - File-name convention: must start with `_` and end with `.json`
//   - Loader gates on `GameConfig.AllowUserLocalization` — if the game build has
//     this disabled, LoadUserLocalizations is a no-op (we log a warning).
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
    // Registers a locale by writing `_<modId>_<localeCode>.json` into the game's
    // user-locale folder and calling LoadUserLocalizations to refresh AvailableLocales.
    // The mod's JSON shape is "key": "translated value" — same as Pratfall's own.
    public static IDisposable Register(string modId, string localeCode, IReadOnlyDictionary<string, string> translations)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));
        if (string.IsNullOrWhiteSpace(localeCode))
            throw new ArgumentException("localeCode is required", nameof(localeCode));
        if (translations == null)
            throw new ArgumentNullException(nameof(translations));

        var json = JsonSerializer.Serialize(translations, new JsonSerializerOptions { WriteIndented = true });
        return RegisterRaw(modId, localeCode, json);
    }

    // Same as Register, but takes the JSON string the mod already built. Useful when
    // the mod prefers to author its locale data in a known schema (e.g. shipping a
    // .json next to its DLL) and just wants the file moved into the right folder.
    public static IDisposable RegisterRaw(string modId, string localeCode, string jsonContent)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));
        if (string.IsNullOrWhiteSpace(localeCode))
            throw new ArgumentException("localeCode is required", nameof(localeCode));
        if (jsonContent == null)
            throw new ArgumentNullException(nameof(jsonContent));

        var dir = GetUserLocaleFolder();
        if (dir == null)
        {
            GD.PrintErr($"[ModFramework] ModLocalizationHelper: could not resolve user locale folder for {modId}/{localeCode}");
            return new NullRegistration();
        }
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Loader requires files to start with `_` and end with `.json`. Mod id +
        // locale code together keep two mods from colliding on the same filename.
        var fileName = $"_{Sanitize(modId)}_{Sanitize(localeCode)}.json";
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
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var userData = platform.GetUserDataPath();
            return string.IsNullOrWhiteSpace(userData) ? null : Path.Combine(userData, "localization");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Could not resolve platform user data path: {ex.Message}");
            return null;
        }
    }

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
