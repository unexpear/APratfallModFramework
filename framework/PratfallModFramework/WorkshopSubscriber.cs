using Godot;
using HarmonyLib;

namespace PratfallModFramework;

// Live Workshop subscription discovery — fires the moment Steam finishes
// downloading a newly-subscribed Workshop item (or one being re-downloaded
// after an update), so users don't have to restart Pratfall to see Workshop
// mods appear in the framework's Mods dialog.
//
// Wiring:
//   1. Harmony POSTFIX on Steam.SetupWorkshopCallbacks — runs after Pratfall
//      registers its own callback, guaranteeing Steamworks SDK is initialized.
//   2. Inside the postfix: subscribe to Steamworks.SteamUGC.OnItemInstalled
//      directly (same event Pratfall's wrapper uses internally). Steamworks
//      multicasts to all subscribers, so we coexist with Pratfall's callback
//      cleanly — neither replaces the other.
//   3. On callback (AppId, PublishedFileId): tell ModManager to rescan its
//      Workshop sources. The new mod folder will be picked up + added to
//      _localMods. ModManager then refreshes any open UI.
//
// Idempotent: Apply() guards against double-install; the SteamUGC handler
// guards against duplicate subscriptions across re-entries.
internal static class WorkshopSubscriber
{
    private static Harmony? _harmony;
    private static bool _applied;
    private static bool _ugcSubscribed;
    private static Action<Steamworks.AppId, Steamworks.Data.PublishedFileId>? _handler;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            _harmony = new Harmony("PratfallModFramework.WorkshopSubscriber");
            var target = AccessTools.Method(typeof(global::Steam), "SetupWorkshopCallbacks");
            if (target == null)
            {
                // Fall back to direct subscribe if Pratfall changes the wrapper name.
                // Steamworks SDK is initialized by Game.Setup which runs before our
                // Initialize on most boots, so this is usually safe.
                TrySubscribeDirect();
                GD.Print("[ModFramework] WorkshopSubscriber: Steam.SetupWorkshopCallbacks not found; tried direct SteamUGC subscribe");
                return;
            }

            var postfix = new HarmonyMethod(typeof(WorkshopSubscriber), nameof(SetupCallbacksPostfix));
            _harmony.Patch(target, postfix: postfix);
            GD.Print("[ModFramework] WorkshopSubscriber: hooked Steam.SetupWorkshopCallbacks — live Workshop subscribes will appear in the Mods dialog without a restart");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber.Apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SetupCallbacksPostfix()
    {
        TrySubscribeDirect();
    }

    private static void TrySubscribeDirect()
    {
        if (_ugcSubscribed) return;
        try
        {
            _handler = OnItemInstalled;
            Steamworks.SteamUGC.OnItemInstalled += _handler;
            _ugcSubscribed = true;
            GD.Print("[ModFramework] WorkshopSubscriber: subscribed to Steamworks.SteamUGC.OnItemInstalled");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber: SteamUGC subscribe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnItemInstalled(Steamworks.AppId appId, Steamworks.Data.PublishedFileId fileId)
    {
        ulong workshopId = fileId.Value;
        GD.Print($"[ModFramework] Workshop item installed: appId={appId} fileId={workshopId} — rescanning sources");
        try
        {
            ModManager.Instance?.NotifyWorkshopItemInstalled(workshopId);
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber: ModManager.NotifyWorkshopItemInstalled threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
