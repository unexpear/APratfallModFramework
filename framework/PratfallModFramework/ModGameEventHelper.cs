using Godot;

namespace PratfallModFramework;

// Helper for subscribing to Pratfall's GameEventBus. The game publishes tagged
// events via `GameEventBus.SendEvent<T>(GameplayTag tag, T eventData)` where T
// implements IGameEvent — a typed pub/sub channel for game-wide notifications.
//
// Per audit (2026-05-16):
//   - Subscription point: static `GameEventBus.OnGameEventReceived` event
//   - Delegate signature: `(GameplayTag tag, IGameEvent eventData)`
//   - Tags are Godot Resources with a `Tag: string` property
//
// Two subscription shapes:
//   - `SubscribeAll(handler)`           — gets every event regardless of tag
//   - `SubscribeToTag(tagString, handler)` — filtered to a specific tag at the bus level
//
// Typical mod usage:
//
//   public static class MyMod
//   {
//       private static IDisposable? _sub;
//       public static void OnLoad()
//       {
//           _sub = ModGameEventHelper.SubscribeToTag("Player.Death", (tag, ev) =>
//               GD.Print($"a player died: {ev}"));
//       }
//       public static void OnUnload() => _sub?.Dispose();
//   }
public static class ModGameEventHelper
{
    // Fires for EVERY game event the bus publishes. Use when the mod wants to
    // observe all traffic (e.g. for logging, analytics, replay capture). Most
    // mods should prefer SubscribeToTag for the obvious cost reason.
    public static IDisposable SubscribeAll(Action<global::GameplayTag, global::IGameEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        global::GameEventReceived callback = (tag, ev) =>
        {
            try { handler(tag, ev); }
            catch (Exception ex) { GD.PrintErr($"[ModFramework] GameEventBus handler threw: {ex.GetType().Name}: {ex.Message}"); }
        };

        global::GameEventBus.OnGameEventReceived += callback;
        return new EventSubscription(callback, "<all>");
    }

    // Fires only for events whose tag string matches. Comparison is ordinal —
    // tag strings are case-sensitive in Pratfall's bus. The bus delivers the
    // event to ALL subscribers (the bus itself doesn't filter), so the
    // filter runs here in the helper — cheap, but worth knowing.
    public static IDisposable SubscribeToTag(string tagString, Action<global::GameplayTag, global::IGameEvent> handler)
    {
        if (string.IsNullOrWhiteSpace(tagString)) throw new ArgumentException("tagString is required", nameof(tagString));
        ArgumentNullException.ThrowIfNull(handler);

        global::GameEventReceived callback = (tag, ev) =>
        {
            if (tag?.Tag != tagString) return;
            try { handler(tag, ev); }
            catch (Exception ex) { GD.PrintErr($"[ModFramework] GameEventBus handler for {tagString} threw: {ex.GetType().Name}: {ex.Message}"); }
        };

        global::GameEventBus.OnGameEventReceived += callback;
        return new EventSubscription(callback, tagString);
    }

    // Reference-based subscription using one of Pratfall's pre-defined
    // `GameplayTags.X` constants. Preferred over the string overload — mod
    // authors get IntelliSense, the comparison goes through
    // `GameplayTag.Equals` (which compares the underlying `.Tag` string,
    // tolerant of separate-instance gotchas), and a typo turns into a
    // compile error instead of a never-fires-handler.
    //
    // Example:
    //   ModGameEventHelper.Subscribe(GameplayTags.Stats_Gameplay_Player_Death,
    //       (tag, ev) => GD.Print("a player died"));
    public static IDisposable Subscribe(global::GameplayTag tag, Action<global::GameplayTag, global::IGameEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(handler);

        global::GameEventReceived callback = (incomingTag, ev) =>
        {
            if (incomingTag == null || !incomingTag.Equals(tag)) return;
            try { handler(incomingTag, ev); }
            catch (Exception ex) { GD.PrintErr($"[ModFramework] GameEventBus handler for {tag.Tag} threw: {ex.GetType().Name}: {ex.Message}"); }
        };

        global::GameEventBus.OnGameEventReceived += callback;
        return new EventSubscription(callback, tag.Tag ?? "<unknown>");
    }

    private sealed class EventSubscription : IDisposable
    {
        private global::GameEventReceived? _callback;
        private readonly string _filter;

        public EventSubscription(global::GameEventReceived callback, string filter)
        {
            _callback = callback;
            _filter = filter;
        }

        public void Dispose()
        {
            if (_callback == null) return;
            try { global::GameEventBus.OnGameEventReceived -= _callback; }
            catch (Exception ex) { GD.PrintErr($"[ModFramework] GameEventBus unsubscribe ({_filter}) failed: {ex.Message}"); }
            _callback = null;
        }
    }
}
