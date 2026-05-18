using System.Collections.Concurrent;
using System.Text;
using Godot;

namespace PratfallModFramework;

// Per-mod logging with file output, godot-console tee, and an in-memory ring
// buffer of recent lines that the crash reporter pulls from when a mod throws.
//
// Usage from a mod:
//   private static readonly IModLogger Log = ModLogger.For("MyMod");
//   public static void OnLoad() { Log.Info("loaded"); Log.Warn("something fishy"); }
//
// File location: <userData>/modframework-logs/<modid>.log (UTF-8, append).
// Console tee: every line is also GD.Print/GD.PrintErr'd with a "[modid]" prefix
// so it appears in godot.log alongside framework messages.
//
// Thread-safe — file writes and ring-buffer mutations are locked per-mod.
public interface IModLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Error(string message, Exception exception);
}

public enum ModLogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

public static class ModLogger
{
    // Capacity of the per-mod ring buffer fed to crash reports.
    private const int RingBufferCapacity = 200;

    private static readonly ConcurrentDictionary<string, ModLoggerInstance> _instances =
        new(StringComparer.OrdinalIgnoreCase);

    // Get or create the logger for a given mod id. Repeated calls return the same
    // instance, so each mod has a single backing log file + ring buffer.
    public static IModLogger For(string modId)
    {
        if (string.IsNullOrWhiteSpace(modId))
            throw new ArgumentException("modId is required", nameof(modId));

        return _instances.GetOrAdd(modId, id => new ModLoggerInstance(id));
    }

    // Used by ModCrashReporter to pull the recent lines for a mod after it threw.
    // Returns an empty list if the mod has no logger yet (never logged).
    internal static IReadOnlyList<string> GetRecentLines(string modId)
    {
        return _instances.TryGetValue(modId, out var inst) ? inst.SnapshotRecent() : Array.Empty<string>();
    }

    // Resolve <userData>/modframework-logs/, creating it on first use. Returns null
    // if the platform user-data path isn't resolvable yet (e.g. very early boot).
    internal static string? ResolveLogFolder()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var raw = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var globalized = ProjectSettings.GlobalizePath(raw);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = raw;
            var dir = Path.Combine(globalized, "modframework-logs");
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ModLoggerInstance : IModLogger
    {
        private readonly string _modId;
        private readonly string _consolePrefix;
        private readonly object _lock = new();
        // Bounded ring of recent log lines (formatted, including timestamp + level).
        private readonly Queue<string> _recent = new(RingBufferCapacity + 1);
        // Resolved lazily so a logger created very early in boot still works once
        // Game.Platform comes up.
        private string? _logFilePath;

        public ModLoggerInstance(string modId)
        {
            _modId = modId;
            _consolePrefix = $"[{modId}]";
        }

        public void Debug(string message) => Write(ModLogLevel.Debug, message, null);
        public void Info(string message) => Write(ModLogLevel.Info, message, null);
        public void Warn(string message) => Write(ModLogLevel.Warn, message, null);
        public void Error(string message) => Write(ModLogLevel.Error, message, null);
        public void Error(string message, Exception exception) => Write(ModLogLevel.Error, message, exception);

        private void Write(ModLogLevel level, string message, Exception? exception)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            var line = exception == null
                ? $"{ts} [{LevelTag(level)}] {message}"
                : $"{ts} [{LevelTag(level)}] {message} | {exception.GetType().Name}: {exception.Message}";

            // Tee to godot.log via GD. Errors go to PrintErr so they show red.
            if (level >= ModLogLevel.Error)
                GD.PrintErr($"{_consolePrefix} {line}");
            else
                GD.Print($"{_consolePrefix} {line}");

            lock (_lock)
            {
                _recent.Enqueue(line);
                while (_recent.Count > RingBufferCapacity)
                    _recent.Dequeue();

                AppendToFile(line);
            }
        }

        private void AppendToFile(string line)
        {
            try
            {
                if (_logFilePath == null)
                {
                    var folder = ResolveLogFolder();
                    if (folder == null) return; // Game.Platform not up yet; in-memory buffer still works.
                    _logFilePath = Path.Combine(folder, $"{Sanitize(_modId)}.log");
                }
                File.AppendAllText(_logFilePath, line + System.Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Don't let logging failures cascade. The console tee + ring buffer
                // are the safety net if the file path is unavailable.
            }
        }

        public IReadOnlyList<string> SnapshotRecent()
        {
            lock (_lock)
            {
                return _recent.ToArray();
            }
        }

        private static string LevelTag(ModLogLevel level) => level switch
        {
            ModLogLevel.Debug => "DEBUG",
            ModLogLevel.Info => "INFO ",
            ModLogLevel.Warn => "WARN ",
            ModLogLevel.Error => "ERROR",
            _ => "?    "
        };

        // Filesystem-safe per-mod file basename. Same rules as elsewhere in the
        // framework (letters/digits/'-', everything else becomes underscore).
        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
