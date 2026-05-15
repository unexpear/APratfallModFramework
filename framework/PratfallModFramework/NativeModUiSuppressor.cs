using System.Reflection;
using Godot;
using HarmonyLib;

namespace PratfallModFramework;

// Pratfall's main menu now ships with its own native ModButton + ModUIViewController
// (added in the 2026-05-15 update). We replace it with our richer framework dialog,
// so we hide the native button to avoid two side-by-side mod buttons in the menu.
//
// The game's MainMenuUIViewController._EnterTree checks ModManager.ShouldHideModLoaderUi
// and calls ModButton.Visible=false when true. The flag has no setter — its getter
// just reads a CLI argument (`--qh-disable-mod-ui`). We Harmony-patch the getter to
// always return true so the menu hides the button on every entry.
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
            var getter = AccessTools.PropertyGetter(typeof(global::ModManager), "ShouldHideModLoaderUi");
            if (getter == null)
            {
                GD.Print("[ModFramework] NativeModUiSuppressor: ShouldHideModLoaderUi getter not found — Pratfall version may not have native mod UI; nothing to suppress");
                return;
            }

            _harmony = new Harmony("PratfallModFramework.NativeModUiSuppressor");
            var prefix = new HarmonyMethod(typeof(NativeModUiSuppressor), nameof(ReturnTrue));
            _harmony.Patch(getter, prefix: prefix);
            GD.Print("[ModFramework] NativeModUiSuppressor: native ModButton will be hidden in main menu");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] NativeModUiSuppressor failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool ReturnTrue(ref bool __result)
    {
        __result = true;
        return false; // skip original
    }
}
