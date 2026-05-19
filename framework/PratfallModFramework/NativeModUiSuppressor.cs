using Godot;
using HarmonyLib;

namespace PratfallModFramework;

// Two Harmony patches on Pratfall's native ModManager that bridge state from
// our framework into vanilla code paths Pratfall still reads, despite having
// turned off the native ModManager loader (see OfficialModBridge).
//
// 1) ShouldHideModLoaderUi getter -> always true.
//    Hides the main menu's native ModButton so our richer framework dialog is
//    the only Mods entry point. The flag has no setter — its real getter
//    reads --qh-disable-mod-ui from CLI; we patch it to always return true.
//
// 2) EnabledModCount getter -> our enabled count.
//    SpeedrunManager.SubmitTimeToLeaderboard refuses to submit if
//    ModManager.EnabledModCount != 0 — an anti-cheat gate that detects mods.
//    With native ModManager turned off, its internal count stays 0 even when
//    our framework has loaded mods. Without this bridge, mods-loaded players
//    would submit "vanilla" runs to the leaderboard. GameOverUIController.Show
//    also reads this for display; the same patch fixes both call sites.
//
// Both patches are no-ops if the targeted symbol isn't found, so future
// Pratfall versions that rename / remove these won't crash the framework —
// they'll just lose that specific bridge.
internal static class NativeModUiSuppressor
{
    private static Harmony? _harmony;
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            _harmony = new Harmony("PratfallModFramework.NativeModUiSuppressor");

            // Patch 1: hide native ModButton.
            var hideUi = AccessTools.PropertyGetter(typeof(global::ModManager), "ShouldHideModLoaderUi");
            if (hideUi != null)
            {
                _harmony.Patch(hideUi, prefix: new HarmonyMethod(typeof(NativeModUiSuppressor), nameof(ShouldHideModLoaderUiPrefix)));
                GD.Print("[ModFramework] NativeModUiSuppressor: native ModButton will be hidden in main menu");
            }
            else
            {
                GD.Print("[ModFramework] NativeModUiSuppressor: ShouldHideModLoaderUi getter not found — Pratfall version may not have native mod UI; nothing to suppress");
            }

            // Patch 2: report our enabled count to native callers (anti-cheat + UI display).
            var enabledCount = AccessTools.PropertyGetter(typeof(global::ModManager), "EnabledModCount");
            if (enabledCount != null)
            {
                _harmony.Patch(enabledCount, prefix: new HarmonyMethod(typeof(NativeModUiSuppressor), nameof(EnabledModCountPrefix)));
                GD.Print("[ModFramework] NativeModUiSuppressor: EnabledModCount bridged to framework state (so SpeedrunManager's anti-cheat gate sees the truth)");
            }
            else
            {
                GD.PrintErr("[ModFramework] NativeModUiSuppressor: EnabledModCount getter NOT found — speedrun leaderboard anti-cheat may be bypassed when mods are loaded!");
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] NativeModUiSuppressor failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ShouldHideModLoaderUiPrefix(ref bool __result)
    {
        __result = true;
        return false; // skip original
    }

    // SpeedrunManager checks `EnabledModCount != 0`. Our ModManager exposes the
    // real count; if the framework isn't initialized yet (this getter can be
    // called early during boot) returning 0 is the correct "no mods loaded yet"
    // answer. If anything goes wrong, fail SAFE — report >0 so anti-cheat refuses
    // to submit rather than letting a modded run onto the leaderboard.
    private static bool EnabledModCountPrefix(ref int __result)
    {
        try
        {
            __result = PratfallModFramework.ModManager.Instance?.EnabledModCount ?? 0;
        }
        catch
        {
            __result = 1;
        }
        return false; // skip original
    }
}
