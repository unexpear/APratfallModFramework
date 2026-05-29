using HarmonyLib;
using Godot;

namespace PratfallModFramework;

// P4.3: symmetric counterpart to SessionStartHooks. Fires when the game returns to the
// main menu — the universal session-end point — so the framework can restore the
// pre-session runtime mod state that P4.3 temporarily applied.
//
// Target (Cecil-confirmed, single overload, not otherwise patched):
//   GameController.LoadMainMenuScene(System.Action onComplete)
// Its IL is a pure scene transition (SceneManager.CancelSceneLoad -> load
// res://scenes/main_menu.tscn -> onComplete), so it is hit on EVERY return to menu
// (host ends, offline ends, leave-lobby-that-returns-to-menu, etc.) regardless of how
// the session ended.
//
// Prefix (not postfix): restore runs while the loader + Godot runtime are still valid for
// the current session, before the scene swap begins. The restore reverts _modEnabled, not
// scene state, so "runtime still alive" is all it needs — and prefix guarantees that.
//
// Called on the Godot main thread (Harmony patch on a Godot method invoked from game
// code), so the restore callback may touch loader/UI APIs directly.
internal static class SessionEndHooks
{
    private static bool _installed;
    private static Action? _onSessionEnd;

    public static void Install(Action onSessionEnd)
    {
        _onSessionEnd = onSessionEnd;
        if (_installed)
            return;

        var harmony = new Harmony("PratfallModFramework.SessionEndHooks");
        harmony.Patch(
            AccessTools.Method(typeof(global::GameController), "LoadMainMenuScene"),
            prefix: new HarmonyMethod(typeof(SessionEndHooks), nameof(BeforeReturnToMainMenuPrefix)));

        _installed = true;
    }

    private static void BeforeReturnToMainMenuPrefix()
    {
        try
        {
            _onSessionEnd?.Invoke();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to restore session runtime state on return to menu: {ex.Message}");
        }
    }
}
