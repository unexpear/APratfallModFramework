using System.Text;
using Godot;

namespace PratfallModFramework;

// Helper for mods that want to persist their own data alongside the game's save.
// Hooks Pratfall's `SavegameManager.OnGameWillSave` event so mod state is flushed
// to disk every time the player triggers a save. Per-mod data lives in a separate
// JSON file under `<userData>/modframework-saves/<modId>.json` — we don't touch
// Pratfall's own savegame format, so a mod's data surviving a game-save schema
// change is fully under the mod's control.
//
// Load is mod-driven (call `LoadIfPresent` at OnLoad time, or read the path
// directly). The game's load lifecycle isn't event-exposed — `SavegameManager.Setup`
// takes a `Action<...> onGameDidLoad` callback that only the game subscribes to.
//
// Typical mod usage:
//
//   public static class MyMod
//   {
//       private static IDisposable? _saveHook;
//       private static MyState _state = new();
//
//       public static void OnLoad()
//       {
//           // Restore prior session's state, if any.
//           var prior = ModSaveDataHelper.LoadIfPresent("MyMod");
//           if (prior != null) _state = JsonSerializer.Deserialize<MyState>(prior) ?? new();
//
//           // Re-serialize on every save.
//           _saveHook = ModSaveDataHelper.Register("MyMod",
//               serialize: () => JsonSerializer.Serialize(_state));
//       }
//       public static void OnUnload() => _saveHook?.Dispose();
//   }
public static class ModSaveDataHelper
{
    // Subscribes `serialize` to fire on the next `OnGameWillSave` and persist the
    // returned string to `<userData>/modframework-saves/<modId>.json`. Dispose to
    // unsubscribe — pair with the mod's OnUnload.
    public static IDisposable Register(string modId, Func<string> serialize)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));
        ArgumentNullException.ThrowIfNull(serialize);

        var path = GetModSaveFilePath(modId);
        if (path == null)
        {
            GD.PrintErr($"[ModFramework] ModSaveDataHelper.Register: could not resolve save path for {modId}");
            return new NullRegistration();
        }

        global::SavegameManager.SaveDataCallback callback = () =>
        {
            try
            {
                var data = serialize();
                if (data == null) return;
                EnsureDir(path);
                File.WriteAllText(path, data, Encoding.UTF8);
                GD.Print($"[ModFramework] {modId} save data flushed ({data.Length} bytes)");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] {modId} save serializer threw: {ex.GetType().Name}: {ex.Message}");
            }
        };

        global::SavegameManager.OnGameWillSave += callback;
        GD.Print($"[ModFramework] {modId} subscribed to OnGameWillSave (will write to {path})");
        return new SaveHookRegistration(callback, modId);
    }

    // Reads the mod's previously-persisted save file if it exists. Returns null
    // when there's no prior data — mod treats that as "first run, start fresh."
    // Safe to call before the game's own save is loaded.
    public static string? LoadIfPresent(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId)) return null;
        var path = GetModSaveFilePath(modId);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] {modId} save read failed: {ex.Message}");
            return null;
        }
    }

    // Deletes the mod's save file. Use sparingly — typically only when the mod
    // wants to reset its state on the user's explicit request.
    public static bool Delete(string modId)
    {
        var path = GetModSaveFilePath(modId);
        if (path == null || !File.Exists(path)) return false;
        try { File.Delete(path); return true; }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] {modId} save delete failed: {ex.Message}");
            return false;
        }
    }

    public static string? GetModSaveFilePath(string modId)
    {
        // Game.Platform.GetUserDataPath() returns a Godot `user://...` URI on Steam.
        // System.IO needs the real filesystem path, so GlobalizePath it.
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var userData = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(userData)) return null;
            var globalized = ProjectSettings.GlobalizePath(userData);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = userData;
            return Path.Combine(globalized, "modframework-saves", Sanitize(modId) + ".json");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Could not resolve mod save path: {ex.Message}");
            return null;
        }
    }

    private static void EnsureDir(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    private static string Sanitize(string s)
    {
        var clean = new StringBuilder();
        foreach (var ch in s)
            clean.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        return clean.ToString();
    }

    private sealed class SaveHookRegistration : IDisposable
    {
        private global::SavegameManager.SaveDataCallback? _callback;
        private readonly string _modId;

        public SaveHookRegistration(global::SavegameManager.SaveDataCallback callback, string modId)
        {
            _callback = callback;
            _modId = modId;
        }

        public void Dispose()
        {
            if (_callback == null) return;
            try { global::SavegameManager.OnGameWillSave -= _callback; }
            catch (Exception ex) { GD.PrintErr($"[ModFramework] {_modId} save unsubscribe failed: {ex.Message}"); }
            _callback = null;
            GD.Print($"[ModFramework] {_modId} unsubscribed from OnGameWillSave");
        }
    }

    private sealed class NullRegistration : IDisposable { public void Dispose() { } }
}
