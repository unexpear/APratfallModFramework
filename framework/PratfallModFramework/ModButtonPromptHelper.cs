using Godot;

namespace PratfallModFramework;

// Helper for showing HUD button-prompt hints from a mod. Wraps
// `ButtonPrompBarController.AddButtonPrompt` so mods don't need to construct
// the `ButtonPromptOptions` resource themselves.
//
// Per audit (2026-05-16):
//   - Singleton: `ButtonPrompBarController.Instance`
//   - `AddButtonPrompt(ButtonPromptOptions options, String context)` — adds a
//     prompt scoped to a `context` string. The controller can clear all prompts
//     for a context via `ClearButtonPrompts(context)` — there's no per-prompt
//     removal, only per-context clearing.
//
// Typical mod usage:
//
//   public static class MyMod
//   {
//       private const string PromptContext = "MyMod_Inventory";
//       public static void OnLoad()
//       {
//           // When the player opens our custom inventory:
//           ModButtonPromptHelper.Show("ui_accept", "Equip", PromptContext);
//           ModButtonPromptHelper.Show("ui_cancel", "Close", PromptContext);
//       }
//       public static void OnUnload()
//       {
//           ModButtonPromptHelper.ClearContext(PromptContext);
//       }
//   }
public static class ModButtonPromptHelper
{
    // Adds a single button prompt to the HUD scoped to `context`. The context
    // string is what the game's clear-all API uses, so picking a unique-per-mod
    // string (e.g. `"<modId>_<screenName>"`) lets you clean up without affecting
    // anyone else's prompts.
    public static void Show(string actionName, string description, string context)
    {
        if (string.IsNullOrWhiteSpace(actionName)) throw new ArgumentException("actionName is required", nameof(actionName));
        if (string.IsNullOrWhiteSpace(context)) throw new ArgumentException("context is required", nameof(context));

        var bar = global::ButtonPrompBarController.Instance;
        if (bar == null)
        {
            GD.PrintErr("[ModFramework] ModButtonPromptHelper.Show: ButtonPrompBarController.Instance is null (HUD not loaded?)");
            return;
        }

        var options = new global::ButtonPromptOptions
        {
            ActionName = actionName,
            Description = description ?? "",
        };

        try
        {
            bar.AddButtonPrompt(options, context);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] AddButtonPrompt failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Clears every prompt registered under the given context. Mirrors the
    // game's `ButtonPrompBarController.ClearButtonPrompts(context)` — there's
    // no per-prompt removal API on the game side, only per-context.
    public static void ClearContext(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return;
        var bar = global::ButtonPrompBarController.Instance;
        if (bar == null) return;
        try
        {
            bar.ClearButtonPrompts(context);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] ClearButtonPrompts failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
