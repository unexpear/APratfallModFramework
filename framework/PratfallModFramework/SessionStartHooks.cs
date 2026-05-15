using HarmonyLib;
using Godot;

namespace PratfallModFramework;

public enum SessionKind
{
    Offline,
    Host,
}

internal static class SessionStartHooks
{
    private static bool _installed;
    private static Action<SessionKind>? _beforeSessionStart;

    public static void Install(Action<SessionKind> beforeSessionStart)
    {
        _beforeSessionStart = beforeSessionStart;
        if (_installed)
            return;

        var harmony = new Harmony("PratfallModFramework.SessionStartHooks");
        harmony.Patch(
            AccessTools.Method(typeof(MainMenuUIViewController), "OnHostButtonClicked"),
            prefix: new HarmonyMethod(typeof(SessionStartHooks), nameof(BeforeHostSessionPrefix)));
        harmony.Patch(
            AccessTools.Method(typeof(MainMenuUIViewController), "OnLocalModeButtonClicked"),
            prefix: new HarmonyMethod(typeof(SessionStartHooks), nameof(BeforeOfflineSessionPrefix)));

        _installed = true;
    }

    private static void BeforeHostSessionPrefix() => Dispatch(SessionKind.Host);
    private static void BeforeOfflineSessionPrefix() => Dispatch(SessionKind.Offline);

    private static void Dispatch(SessionKind kind)
    {
        try
        {
            _beforeSessionStart?.Invoke(kind);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] Failed to apply desired mod state before session start: {ex.Message}");
        }
    }
}
