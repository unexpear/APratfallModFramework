using Godot;

namespace PratfallModFramework;

// Helper for the drop-pool extension pattern Robert recommended on Discord (5/13/26)
// when the framework can't register new C# Node types at runtime. Mods load an existing
// `RandomWeightedDropPool` resource, append a `RandomWeightedScene` entry, and the helper
// remembers exactly what was added so OnUnload can put the pool back to its original
// shape without leaving a stale entry behind.
//
// Typical mod usage:
//
//   public static class MyMod
//   {
//       private static IDisposable? _registration;
//       public static void OnLoad()
//       {
//           _registration = ModDropPoolHelper.Register(
//               poolResPath: "res://path/to/SomeDropPool.tres",
//               scene: GD.Load<PackedScene>("res://my_mod/MyItem.tscn"),
//               weight: 5);
//       }
//       public static void OnUnload() => _registration?.Dispose();
//   }
public static class ModDropPoolHelper
{
    public static IDisposable Register(string poolResPath, PackedScene scene, int weight,
        int weightAdvantage = 0, int weightDisadvantage = 0, bool canDropSingleplayer = true)
    {
        if (string.IsNullOrWhiteSpace(poolResPath))
            throw new ArgumentException("poolResPath is required", nameof(poolResPath));
        var pool = ResourceLoader.Load<RandomWeightedDropPool>(poolResPath);
        if (pool == null)
            throw new InvalidOperationException($"RandomWeightedDropPool not found at {poolResPath}");
        return RegisterIn(pool, scene, weight, weightAdvantage, weightDisadvantage, canDropSingleplayer, poolResPath);
    }

    // Same as Register, but takes an already-loaded pool. Useful for tests that want to
    // exercise the helper against an in-memory pool without touching shipped game data.
    public static IDisposable RegisterIn(RandomWeightedDropPool pool, PackedScene scene, int weight,
        int weightAdvantage = 0, int weightDisadvantage = 0, bool canDropSingleplayer = true,
        string label = "<in-memory>")
    {
        if (pool == null)
            throw new ArgumentNullException(nameof(pool));
        if (scene == null)
            throw new ArgumentNullException(nameof(scene));

        var entry = new RandomWeightedScene
        {
            Scene = scene,
            Weight = weight,
            WeightAdvantage = weightAdvantage == 0 ? weight : weightAdvantage,
            WeightDisadvantage = weightDisadvantage == 0 ? weight : weightDisadvantage,
            CanDropSingleplayer = canDropSingleplayer,
        };

        var existing = pool.Pool ?? Array.Empty<RandomWeightedScene>();
        var grown = new RandomWeightedScene[existing.Length + 1];
        Array.Copy(existing, grown, existing.Length);
        grown[existing.Length] = entry;
        pool.Pool = grown;

        GD.Print($"[ModFramework] Drop-pool registered: +1 entry (weight={weight}) on {label}");
        return new PoolRegistration(pool, entry, label);
    }

    private sealed class PoolRegistration : IDisposable
    {
        private RandomWeightedDropPool? _pool;
        private RandomWeightedScene? _entry;
        private readonly string _path;

        public PoolRegistration(RandomWeightedDropPool pool, RandomWeightedScene entry, string path)
        {
            _pool = pool;
            _entry = entry;
            _path = path;
        }

        public void Dispose()
        {
            if (_pool == null || _entry == null) return;
            var current = _pool.Pool;
            if (current == null) { _pool = null; _entry = null; return; }

            // Remove only the specific entry we added — never compare by content, because
            // two mods could legitimately register the same scene at the same weight.
            var idx = Array.IndexOf(current, _entry);
            if (idx < 0) { _pool = null; _entry = null; return; }

            var shrunk = new RandomWeightedScene[current.Length - 1];
            Array.Copy(current, 0, shrunk, 0, idx);
            Array.Copy(current, idx + 1, shrunk, idx, current.Length - idx - 1);
            _pool.Pool = shrunk;

            GD.Print($"[ModFramework] Drop-pool unregistered: -1 entry on {_path}");
            _pool = null;
            _entry = null;
        }
    }
}
