using System.Text;
using Godot;

namespace PratfallModFramework;

// Writes structured crash reports when a mod throws inside ModInit / OnLoad /
// OnUnload / Harmony patch loading. Each report is a single text file at
// <userData>/modframework-crash-reports/<modid>_<utc-timestamp>.txt that
// includes:
//
//   - timestamp + context (which lifecycle point threw)
//   - manifest snapshot (id, name, version, author, multiplayer mode)
//   - exception type + message + full stack, walking the InnerException chain
//   - the last ~200 lines from that mod's ModLogger ring buffer
//
// Mod authors can ask users for the file when troubleshooting; users can grep
// the crash-reports folder for whoever's mod hit recently.
//
// Failure to write a crash report is silent and never blocks unloading the mod
// — the GD.PrintErr tee from the catch site is the safety net.
public static class ModCrashReporter
{
    public static void Report(string modId, string context, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(modId) || exception == null) return;

        try
        {
            var folder = ResolveCrashReportFolder();
            if (folder == null) return;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH.mm.ss");
            var path = Path.Combine(folder, $"{Sanitize(modId)}_{ts}.txt");

            var body = BuildReportBody(modId, context, exception);
            File.WriteAllText(path, body, Encoding.UTF8);
            GD.PrintErr($"[ModFramework] Crash report written: {path}");
        }
        catch (Exception writeEx)
        {
            // Don't cascade. The original exception's GD.PrintErr from the catch
            // site is still in godot.log; the crash report was a best-effort extra.
            GD.PrintErr($"[ModFramework] ModCrashReporter failed to write report for {modId}: {writeEx.GetType().Name}: {writeEx.Message}");
        }
    }

    private static string BuildReportBody(string modId, string context, Exception exception)
    {
        var sb = new StringBuilder();
        sb.Append("Pratfall Mod Framework — crash report\n");
        sb.Append("======================================\n");
        sb.Append($"Mod id     : {modId}\n");
        sb.Append($"Context    : {context}\n");
        sb.Append($"UTC time   : {DateTime.UtcNow:O}\n");
        sb.Append($"Local time : {DateTime.Now:O}\n");
        sb.Append("\n");

        AppendManifestSnapshot(sb, modId);

        sb.Append("Exception\n");
        sb.Append("---------\n");
        AppendExceptionChain(sb, exception);

        sb.Append("\nRecent log lines (from ModLogger ring buffer)\n");
        sb.Append("---------------------------------------------\n");
        var recent = ModLogger.GetRecentLines(modId);
        if (recent.Count == 0)
        {
            sb.Append("(no entries — mod never called ModLogger.For(modId).Info/Warn/Error)\n");
        }
        else
        {
            foreach (var line in recent)
                sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static void AppendManifestSnapshot(StringBuilder sb, string modId)
    {
        sb.Append("Manifest\n");
        sb.Append("--------\n");
        try
        {
            // Look up the manifest from the framework's own loaded set, falling
            // back to "unknown" fields if the mod isn't registered (e.g. crash
            // happened during very early load).
            var manifest = TryGetManifest(modId);
            if (manifest == null)
            {
                sb.Append("(manifest not available — mod was not in framework's loaded set at crash time)\n\n");
                return;
            }
            sb.Append($"Id         : {manifest.Id}\n");
            sb.Append($"Name       : {manifest.Name}\n");
            sb.Append($"Version    : {manifest.Version}\n");
            sb.Append($"Author     : {manifest.Author}\n");
            sb.Append($"Type       : {manifest.Type}\n");
            sb.Append($"Multiplayer: {manifest.Multiplayer?.Mode ?? "<none>"}\n");
            sb.Append('\n');
        }
        catch (Exception manifestEx)
        {
            sb.Append($"(manifest lookup threw: {manifestEx.GetType().Name}: {manifestEx.Message})\n\n");
        }
    }

    // Best-effort manifest lookup. Tries ManifestManager's loaded manifests; if
    // the mod isn't there, returns null and the report just notes that.
    private static ModManifest? TryGetManifest(string modId)
    {
        try
        {
            // ManifestManager is a static scanner; iterate any cached manifests.
            // Falls back to file-system scan if nothing's cached yet.
            var scanRoot = ResolveModsRoot();
            if (scanRoot == null || !Directory.Exists(scanRoot)) return null;

            foreach (var sub in Directory.EnumerateDirectories(scanRoot))
            {
                var manifestPath = Path.Combine(sub, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var json = File.ReadAllText(manifestPath);
                    var manifest = ModManifest.FromJson(json, directoryName: Path.GetFileName(sub), directoryPath: sub);
                    if (string.Equals(manifest.Id, modId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(Path.GetFileName(sub), modId, StringComparison.OrdinalIgnoreCase))
                    {
                        return manifest;
                    }
                }
                catch
                {
                    // Skip bad manifests; we're best-effort.
                }
            }
        }
        catch
        {
        }
        return null;
    }

    private static void AppendExceptionChain(StringBuilder sb, Exception exception)
    {
        var current = exception;
        var depth = 0;
        while (current != null)
        {
            var prefix = depth == 0 ? "" : $"InnerException[{depth}] -> ";
            sb.Append($"{prefix}{current.GetType().FullName}: {current.Message}\n");
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                sb.Append(current.StackTrace);
                sb.Append('\n');
            }
            sb.Append('\n');
            current = current.InnerException;
            depth++;
            if (depth > 8) break; // guard against degenerate cycles
        }
    }

    private static string? ResolveCrashReportFolder()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var raw = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var globalized = ProjectSettings.GlobalizePath(raw);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = raw;
            return Path.Combine(globalized, "modframework-crash-reports");
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveModsRoot()
    {
        try
        {
            var platform = global::Game.Platform;
            if (platform == null) return null;
            var raw = platform.GetUserDataPath();
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var globalized = ProjectSettings.GlobalizePath(raw);
            if (string.IsNullOrWhiteSpace(globalized)) globalized = raw;
            return Path.Combine(globalized, "mods");
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
        return sb.ToString();
    }
}
