using Godot;
using HarmonyLib;

namespace PratfallModFramework;

public static class BuiltInPatches
{
    private static Harmony? _harmony;
    private static bool _applied;

    public static void Apply()
    {
        if (_applied) return;
        _applied = true;

        _harmony = new Harmony("PratfallModFramework.BuiltInPatches");

        var target = AccessTools.Method(typeof(PlayerPickaxeComponent), "TriggerPrimaryAction");
        if (target == null)
        {
            GD.PrintErr("[ModFramework] BuiltInPatches: PlayerPickaxeComponent.TriggerPrimaryAction not found");
            return;
        }

        var postfix = new HarmonyMethod(typeof(BuiltInPatches), nameof(OnPickaxeSwingPostfix));
        _harmony.Patch(target, postfix: postfix);
        GD.Print("[ModFramework] BuiltInPatches: dog barks on pickaxe swing");
    }

    private static void OnPickaxeSwingPostfix(PlayerPickaxeComponent __instance)
    {
        try
        {
            var barkComp = __instance.Entity?.DogBarkComponent;
            if (barkComp != null)
            {
                var node = __instance.GetEntity<Godot.Node3D>();
                barkComp.PlayBarkAt(node?.GlobalPosition ?? Vector3.Zero, true);
            }
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[ModFramework] BarkOnPickaxe error: {ex.Message}");
        }
    }
}
