using Godot;

namespace PratfallModFramework;

// "Two mods declared incompatible — keep which?" prompt. Pops when the
// compatibility checker detects two locally-enabled mods that declare each
// other in conflictsWith. Three buttons: Keep A / Keep B / Decide later.
// The loser is disabled and the dialog toggle is synced so the Mods dialog
// reflects the new state. CanvasLayer 130 so it can appear on top of the
// Mods dialog (128) if the user is still in the Mods menu.
public static partial class MainMenuIntegration
{
    public static void ShowConflictPrompt(SceneTree tree, string modAId, string modAName,
        string modBId, string modBName, string reason, Action<string?> onChosen)
    {
        if (tree?.Root == null) { onChosen(null); return; }

        var existing = tree.Root.GetNodeOrNull("ModFrameworkConflictLayer");
        if (existing != null) { onChosen(null); return; } // one prompt at a time

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkConflictLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control
        {
            Name = "ModFrameworkConflictDialog",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);

        // Rebind _tree so CreateFallbackDialogHost's harvest helpers work in this context.
        _tree ??= tree;

        // Conflict prompt is small content (~6 lines + 3 buttons). A compact dialog hugs
        // its content instead of stretching to the Mods-dialog floor.
        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        var dialogSize = new Vector2(
            Mathf.Clamp(viewportSize.X * 0.42f, 480f, 640f),
            0f /* compact host ignores Y floor */);
        var panel = CreateFallbackDialogHost(overlay, dialogSize, compact: true);

        var title = new Label
        {
            Text = "Mod Conflict",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(title, Math.Max(_buttonFontSize + 10, 26));
        title.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        panel.AddChild(title);

        var body = new Label
        {
            Text = $"{modAName} and {modBName} are declared incompatible.\n\n{reason}\n\nWhich one should stay enabled?",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(body, Math.Max(_buttonFontSize, 16));
        body.AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 0.98f));
        panel.AddChild(body);

        var buttonRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        buttonRow.AddThemeConstantOverride("separation", 18);
        panel.AddChild(buttonRow);

        var buttonHeight = Math.Max(GetReferenceButtonHeight(), 56f);
        var buttonWidth = Mathf.Clamp(dialogSize.X * 0.35f, 200f, 320f);

        var keepA = new Button { Text = $"Keep {modAName}", FocusMode = Control.FocusModeEnum.All };
        ApplyButtonTheme(keepA);
        keepA.CustomMinimumSize = new Vector2(buttonWidth, buttonHeight);
        buttonRow.AddChild(keepA);

        var keepB = new Button { Text = $"Keep {modBName}", FocusMode = Control.FocusModeEnum.All };
        ApplyButtonTheme(keepB);
        keepB.CustomMinimumSize = new Vector2(buttonWidth, buttonHeight);
        buttonRow.AddChild(keepB);

        var laterBtn = new Button
        {
            Text = "Decide later",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        ApplyButtonTheme(laterBtn);
        laterBtn.CustomMinimumSize = new Vector2(220f, Math.Max(GetReferenceButtonHeight() * 0.85f, 44f));
        panel.AddChild(laterBtn);

        bool fired = false;
        void Resolve(string? keepId)
        {
            if (fired) return;
            fired = true;
            canvasLayer.QueueFree();
            onChosen(keepId);
        }

        keepA.Pressed += () => Resolve(modAId);
        keepB.Pressed += () => Resolve(modBId);
        laterBtn.Pressed += () => Resolve(null);
        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            Resolve(null);
            overlay.AcceptEvent();
        };

        WireVerticalFocus(new List<Control> { keepA, keepB, laterBtn });
        keepA.CallDeferred("grab_focus");
    }
}
