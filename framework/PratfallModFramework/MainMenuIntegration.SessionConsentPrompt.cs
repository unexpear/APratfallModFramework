using Godot;

namespace PratfallModFramework;

// P4.2: per-player local consent prompt for the actions emitted by SessionApplyPlanner.
// Strictly local: no network calls, no loader calls, no mutations. The caller
// (SessionConsentCoordinator) decides what to record based on the returned decision.
// Modeled on ShowAcquisitionPrompt — CanvasLayer 131 (one above acquisition's 130 so
// the two can coexist visually, though they don't overlap in practice). Esc maps to
// LeaveRequired so an accidental dismiss is treated as a safe decline.
public static partial class MainMenuIntegration
{
    public static void ShowSessionConsentPrompt(SceneTree tree,
        string title, string body,
        IReadOnlyList<(string Label, SessionConsentDecision Decision)> choices,
        Action<SessionConsentDecision> onResolve)
    {
        if (tree?.Root == null) { onResolve(SessionConsentDecision.LeaveRequired); return; }
        if (choices == null || choices.Count == 0) { onResolve(SessionConsentDecision.LeaveRequired); return; }

        var existing = tree.Root.GetNodeOrNull("ModFrameworkSessionConsentLayer");
        if (existing != null) existing.QueueFree();

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkSessionConsentLayer", Layer = 131 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkSessionConsentDialog", MouseFilter = Control.MouseFilterEnum.Stop };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);

        _tree ??= tree;

        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        var dialogSize = new Vector2(Mathf.Clamp(viewportSize.X * 0.42f, 480f, 640f), 0f);
        var panel = CreateFallbackDialogHost(overlay, dialogSize, compact: true);

        var titleLabel = new Label
        {
            Text = title,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(titleLabel, Math.Max(_buttonFontSize + 10, 26));
        titleLabel.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        panel.AddChild(titleLabel);

        var bodyLabel = new Label
        {
            Text = body,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(bodyLabel, Math.Max(_buttonFontSize, 16));
        bodyLabel.AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 0.98f));
        panel.AddChild(bodyLabel);

        var buttonRow = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        buttonRow.AddThemeConstantOverride("separation", 10);
        panel.AddChild(buttonRow);

        var buttonHeight = Math.Max(GetReferenceButtonHeight(), 52f);
        var buttonWidth = Mathf.Clamp(dialogSize.X * 0.7f, 320f, 480f);
        var focusables = new List<Control>();

        // Single-shot resolve guard mirrors the acquisition prompt: prevents two
        // button presses (or a press + an Esc) from firing the callback twice.
        bool fired = false;
        void Resolve(SessionConsentDecision decision)
        {
            if (fired) return;
            fired = true;
            canvasLayer.QueueFree();
            onResolve(decision);
        }

        foreach (var choice in choices)
        {
            var btn = new Button
            {
                Text = choice.Label,
                FocusMode = Control.FocusModeEnum.All,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            ApplyButtonTheme(btn);
            btn.CustomMinimumSize = new Vector2(buttonWidth, buttonHeight);
            var d = choice.Decision;
            btn.Pressed += () => Resolve(d);
            buttonRow.AddChild(btn);
            focusables.Add(btn);
        }

        // Esc / ui_cancel → safest decline. For CannotContinue (only an "OK"
        // button → Acknowledged) we still want Esc to dismiss cleanly, so we
        // resolve with the LAST listed decision's "decline-equivalent": if the
        // only button is Acknowledged, Esc resolves Acknowledged; otherwise
        // LeaveRequired. The rule below picks the LeaveRequired button if any
        // is present, else the last button. Keeps Esc safe across all 5 cases.
        var escDecision = SessionConsentDecision.LeaveRequired;
        bool hasLeaveRequired = false;
        foreach (var c in choices) if (c.Decision == SessionConsentDecision.LeaveRequired) { hasLeaveRequired = true; break; }
        if (!hasLeaveRequired) escDecision = choices[choices.Count - 1].Decision;

        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            Resolve(escDecision);
            overlay.AcceptEvent();
        };

        WireVerticalFocus(focusables);
        if (focusables.Count > 0) focusables[0].CallDeferred("grab_focus");
    }
}
