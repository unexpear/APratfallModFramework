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
// Idempotent at three layers:
//   - Apply() won't double-install (guarded by _applied)
//   - TrySubscribeDirect won't double-subscribe (guarded by _ugcSubscribed)
//   - OnItemInstalled won't double-dispatch the same workshopId within a
//     short window (guarded by _recentlyProcessed) — Steam can re-fire the
//     event during startup / validation passes; second dispatch is a no-op
//     anyway since the rescan would just hit a ContainsKey collision, but
//     the dedup keeps the log quiet.
internal static class WorkshopSubscriber
{
    private static Harmony? _harmony;
    private static bool _applied;
    private static bool _ugcSubscribed;
    // Held so Shutdown can `-=` the same delegate instance we attached.
    // Steamworks event removal needs the original delegate reference; storing
    // the method group via `+= OnItemInstalled` works for adding but doesn't
    // give us a handle for removing later.
    private static Action<Steamworks.AppId, Steamworks.Data.PublishedFileId>? _handler;
    // Per-workshopId dispatch dedup. Steam may re-fire OnItemInstalled for the
    // same item in quick succession (validation passes, install-event replays
    // during boot). Window is short enough to NOT swallow legitimate updates
    // — a real re-download takes seconds, not milliseconds.
    private static readonly Dictionary<ulong, DateTime> _recentlyProcessed = new();
    private static readonly TimeSpan DispatchDedupeWindow = TimeSpan.FromSeconds(2);

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        try
        {
            _harmony = new Harmony("PratfallModFramework.WorkshopSubscriber");
            var target = AccessTools.Method(typeof(global::Steam), "SetupWorkshopCallbacks");
            if (target != null)
            {
                var postfix = new HarmonyMethod(typeof(WorkshopSubscriber), nameof(SetupCallbacksPostfix));
                _harmony.Patch(target, postfix: postfix);
                GD.Print("[ModFramework] WorkshopSubscriber: hooked Steam.SetupWorkshopCallbacks postfix");
            }
            else
            {
                GD.Print("[ModFramework] WorkshopSubscriber: Steam.SetupWorkshopCallbacks not found — relying on direct SteamUGC subscribe only");
            }

            // Also subscribe immediately. Harmony postfixes don't fire retroactively,
            // so if Pratfall already called Steam.SetupWorkshopCallbacks before our
            // framework initialized, the postfix above never runs. TrySubscribeDirect
            // is guarded by _ugcSubscribed so it's a no-op when the postfix also fires.
            TrySubscribeDirect();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber.Apply failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Symmetric teardown — unsubscribes from Steamworks + unpatches Harmony so
    // a fresh Apply() call after Shutdown works correctly. Called from
    // ModManager.Shutdown / Bootstrap.Shutdown lifecycle hooks. Safe to call
    // multiple times; safe to call when Apply was never run.
    public static void Shutdown()
    {
        if (_ugcSubscribed && _handler != null)
        {
            try { Steamworks.SteamUGC.OnItemInstalled -= _handler; }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[ModFramework] WorkshopSubscriber.Shutdown: SteamUGC unsubscribe threw: {ex.GetType().Name}: {ex.Message}");
            }
            _ugcSubscribed = false;
            _handler = null;
        }

        if (_harmony != null)
        {
            try { _harmony.UnpatchAll(_harmony.Id); }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[ModFramework] WorkshopSubscriber.Shutdown: Harmony unpatch threw: {ex.GetType().Name}: {ex.Message}");
            }
            _harmony = null;
        }

        _applied = false;
        _recentlyProcessed.Clear();
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
            GD.Print("[ModFramework] WorkshopSubscriber: subscribed to Steamworks.SteamUGC.OnItemInstalled — live Workshop subscribes will appear in the Mods dialog without a restart");
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber: SteamUGC subscribe failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OnItemInstalled(Steamworks.AppId appId, Steamworks.Data.PublishedFileId fileId)
    {
        ulong workshopId = fileId.Value;

        // Per-workshopId dedup. Steam can re-fire the same item in rapid
        // succession (validation passes, startup install-event replays).
        // Without this, ModManager.NotifyWorkshopItemInstalled would still
        // be idempotent (rescan + ContainsKey collision), but the log would
        // shout the same workshopId multiple times for no useful reason.
        // Window is 2s — much shorter than a real Workshop update cycle, so
        // legitimate re-installs aren't suppressed.
        var now = DateTime.UtcNow;
        if (_recentlyProcessed.TryGetValue(workshopId, out var lastTime)
            && now - lastTime < DispatchDedupeWindow)
        {
            return;
        }
        _recentlyProcessed[workshopId] = now;
        // Cheap GC: drop entries whose window has elapsed, only when the map
        // grows beyond a sensible bound (keeps cost amortized).
        if (_recentlyProcessed.Count > 32)
        {
            var stale = _recentlyProcessed
                .Where(kv => now - kv.Value > DispatchDedupeWindow)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in stale) _recentlyProcessed.Remove(key);
        }

        GD.Print($"[ModFramework] Workshop item installed: appId={appId} fileId={workshopId} — rescanning sources");
        // Facepunch.Steamworks dispatches OnItemInstalled from its callback pump,
        // which may or may not be on Godot's main thread. ModManager mutates
        // _localMods + several dictionaries; doing that off-thread could race
        // with main-thread iteration. Marshal via Callable+CallDeferred so the
        // re-scan + UI refresh always happen on the main thread.
        try
        {
            Callable.From(() =>
            {
                try { ModManager.Instance?.NotifyWorkshopItemInstalled(workshopId); }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"[ModFramework] WorkshopSubscriber: ModManager.NotifyWorkshopItemInstalled threw: {ex.GetType().Name}: {ex.Message}");
                }
            }).CallDeferred();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] WorkshopSubscriber: CallDeferred dispatch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
