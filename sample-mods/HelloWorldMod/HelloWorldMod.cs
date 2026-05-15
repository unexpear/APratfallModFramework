using Godot;

namespace HelloWorldMod;

// Minimal Pratfall mod. The framework discovers any static `OnLoad`/`OnUnload` method
// in any type and calls them when the mod is enabled / disabled. That's the whole
// contract for a "hello world" mod — no base class, no inheritance.
//
// To add gameplay behavior, decorate types with [ModPatch(...)] and provide static
// Prefix/Postfix/Transpiler methods (Harmony-compatible signatures). See AGENTS.md
// for the patch contract and the framework's voting/transfer rules.
public static class HelloWorldMod
{
    public static void OnLoad()
    {
        GD.Print("[HelloWorldMod] Loaded. Hello from a Pratfall mod!");
    }

    public static void OnUnload()
    {
        GD.Print("[HelloWorldMod] Unloaded.");
    }
}
