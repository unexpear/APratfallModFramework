using HarmonyLib;
using Godot;
using System.Text.Json;

namespace PratfallModFramework;

internal static class OfficialModBridge
{
    private static bool _installed;
    private static bool _hasLoggedReadInterception;

    public static void Install()
    {
        if (_installed)
            return;

        var harmony = new Harmony("PratfallModFramework.OfficialModBridge");
        harmony.Patch(
            AccessTools.Method(typeof(global::ModManager), "ReadLoadedModsFromFile"),
            prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(ReadLoadedModsFromFilePrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(global::ModManager), "WriteLoadedModsToFile"),
            prefix: new HarmonyMethod(typeof(OfficialModBridge), nameof(WriteLoadedModsToFilePrefix)));

        _installed = true;
        GD.Print("[ModFramework] Official mod loader bubble installed");
    }

    public static bool EnableMod(ModManifest manifest)
    {
        if (!TryResolveManifest(manifest, out var officialManifest))
            return false;

        return global::ModManager.EnableMod(officialManifest);
    }

    public static bool DisableMod(ModManifest manifest)
    {
        if (!TryResolveManifest(manifest, out var officialManifest))
            return false;

        return global::ModManager.DisableMod(officialManifest);
    }

    public static bool IsEnabled(ModManifest manifest)
    {
        if (!TryResolveManifest(manifest, out var officialManifest))
            return false;

        return global::ModManager.IsModEnabled(officialManifest);
    }

    public static bool CanResolveManifest(ModManifest manifest)
    {
        return !string.IsNullOrWhiteSpace(manifest.DirectoryName) &&
               global::ModManager.GetModManifest(manifest.DirectoryName) != null;
    }

    public static HashSet<string> ReadPhysicalEnabledDirectories()
    {
        var path = GetPhysicalEnabledModsFilePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var directories = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), BubbleJson.Options)
                ?? new List<string>();
            return new HashSet<string>(
                ModManifestJson.NormalizeIdentifiers(directories),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to read built-in enabled_mods.json: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool ReadLoadedModsFromFilePrefix(ref List<string> __result)
    {
        __result = new List<string>();
        if (!_hasLoggedReadInterception)
        {
            _hasLoggedReadInterception = true;
            GD.Print("[ModFramework] Built-in enabled_mods.json read intercepted; startup official mods are delayed until framework apply");
        }
        return false;
    }

    private static bool WriteLoadedModsToFilePrefix(ref bool __result)
    {
        __result = true;
        return false;
    }

    private static bool TryResolveManifest(ModManifest manifest, out global::ModManifest officialManifest)
    {
        officialManifest = null!;
        if (string.IsNullOrWhiteSpace(manifest.DirectoryName))
            return false;

        officialManifest = global::ModManager.GetModManifest(manifest.DirectoryName);
        return officialManifest != null;
    }

    private static string? GetPhysicalEnabledModsFilePath()
    {
        var gameDir = Path.GetDirectoryName(OS.GetExecutablePath());
        if (string.IsNullOrWhiteSpace(gameDir))
            return null;

        return Path.Combine(gameDir, "mods", "enabled_mods.json");
    }

    private static class BubbleJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true
        };
    }
}
