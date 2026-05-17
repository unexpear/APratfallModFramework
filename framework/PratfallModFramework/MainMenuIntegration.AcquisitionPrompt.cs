using Godot;

namespace PratfallModFramework;

// Per-peer acquisition prompt — fires after a vote passes for a mod the local
// player doesn't have. Three buttons:
//   Download                — transfer the files from the peer that has them
//   Use settings only       — stretch the mod's settings locally (shown only
//                             when the mod is stretch-applicable)
//   Decline (leave lobby)   — leaves the lobby because the session can't
//                             continue with state divergence
// CanvasLayer 130 so it sits above the Mods dialog at 128.
public static partial class MainMenuIntegration
{
    public static void ShowAcquisitionPrompt(SceneTree tree,
        string modName, string modVersion,
        bool canStretch, long? approxDownloadBytes,
        Action onDownload, Action onStretch, Action onDecline)
    {
        if (tree?.Root == null) { onDecline(); return; }
        var existing = tree.Root.GetNodeOrNull("ModFrameworkAcquisitionLayer");
        if (existing != null) { existing.QueueFree(); }

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkAcquisitionLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkAcquisitionDialog", MouseFilter = Control.MouseFilterEnum.Stop };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);

        _tree ??= tree;

        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        var dialogSize = new Vector2(Mathf.Clamp(viewportSize.X * 0.42f, 480f, 640f), 0f);
        var panel = CreateFallbackDialogHost(overlay, dialogSize, compact: true);

        var title = new Label
        {
            Text = "Mod Required",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(title, Math.Max(_buttonFontSize + 10, 26));
        title.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        panel.AddChild(title);

        var sizeHint = approxDownloadBytes.HasValue ? $" (~{approxDownloadBytes.Value / 1024} KB)" : "";
        var body = new Label
        {
            Text = $"The lobby voted to enable {modName} {modVersion} and you don't have it.\n\nHow do you want to handle it?",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(body, Math.Max(_buttonFontSize, 16));
        body.AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 0.98f));
        panel.AddChild(body);

        var buttonRow = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        buttonRow.AddThemeConstantOverride("separation", 10);
        panel.AddChild(buttonRow);

        var buttonHeight = Math.Max(GetReferenceButtonHeight(), 52f);
        var buttonWidth = Mathf.Clamp(dialogSize.X * 0.7f, 320f, 480f);
        var focusables = new List<Control>();

        bool fired = false;
        void Resolve(Action choice)
        {
            if (fired) return;
            fired = true;
            canvasLayer.QueueFree();
            choice();
        }

        var dlBtn = new Button
        {
            Text = $"Download{sizeHint}",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        ApplyButtonTheme(dlBtn);
        dlBtn.CustomMinimumSize = new Vector2(buttonWidth, buttonHeight);
        dlBtn.Pressed += () => Resolve(onDownload);
        buttonRow.AddChild(dlBtn);
        focusables.Add(dlBtn);

        if (canStretch)
        {
            var stretchBtn = new Button
            {
                Text = "Use settings only (stretch)",
                FocusMode = Control.FocusModeEnum.All,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            ApplyButtonTheme(stretchBtn);
            stretchBtn.CustomMinimumSize = new Vector2(buttonWidth, buttonHeight);
            stretchBtn.Pressed += () => Resolve(onStretch);
            buttonRow.AddChild(stretchBtn);
            focusables.Add(stretchBtn);
        }

        var declineBtn = new Button
        {
            Text = "Decline (leave lobby)",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
        };
        ApplyButtonTheme(declineBtn);
        declineBtn.CustomMinimumSize = new Vector2(buttonWidth, Math.Max(buttonHeight * 0.85f, 44f));
        declineBtn.Pressed += () => Resolve(onDecline);
        buttonRow.AddChild(declineBtn);
        focusables.Add(declineBtn);

        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            Resolve(onDecline);
            overlay.AcceptEvent();
        };

        WireVerticalFocus(focusables);
        dlBtn.CallDeferred("grab_focus");
    }
}
