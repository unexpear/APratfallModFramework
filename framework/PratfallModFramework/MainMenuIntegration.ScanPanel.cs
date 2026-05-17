using Godot;

namespace PratfallModFramework;

// IL safety scanner panel — fired from the 🔍 button on a mod card. Shows a
// finding list grouped by severity (Danger / Warning / Info), each with the
// dangerous API and the call-site method. Clean mods get a green ✓ summary.
// Running this panel IS the user-consent action — ModManager.ScanMod marks the
// mod's fingerprint as user-checked, releasing the load gate. CanvasLayer 130
// so it sits above the Mods dialog at 128.
//
// Consent-action distinction: the read-only metadata counterpart is
// ShowInspectionPanel (ℹ button), which surfaces manifest facts without
// touching the gate.
public static partial class MainMenuIntegration
{
    public static void ShowScanPanel(SceneTree tree, ModScanner.Report report)
    {
        if (tree?.Root == null || report == null) return;
        var existing = tree.Root.GetNodeOrNull("ModFrameworkScanLayer");
        if (existing != null) existing.QueueFree();

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkScanLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkScanDialog", MouseFilter = Control.MouseFilterEnum.Stop };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);

        _tree ??= tree;

        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        var dialogSize = new Vector2(
            Mathf.Clamp(viewportSize.X * 0.55f, 600f, 820f),
            Mathf.Clamp(viewportSize.Y * 0.72f, 460f, 720f));
        var panel = CreateFallbackDialogHost(overlay, dialogSize, compact: false);

        var title = new Label
        {
            Text = $"Scan: {report.ModId}",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(title, Math.Max(_buttonFontSize + 8, 24));
        title.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        panel.AddChild(title);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        panel.AddChild(scroll);

        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(body);

        // Top summary
        if (!report.ScannedSuccessfully && report.ScanErrors.Count > 0)
        {
            AddInspectHeader(body, "Scan could not run");
            foreach (var err in report.ScanErrors)
                AddScanLine(body, "  " + err, new Color(0.95f, 0.55f, 0.45f));
        }
        else if (report.Findings.Count == 0)
        {
            AddInspectHeader(body, "Result");
            AddScanLine(body,
                $"✓ Clean — scanned {report.MethodsScanned} methods, no concerning API usage found.",
                new Color(0.55f, 0.9f, 0.55f));
            AddInspectMuted(body,
                "This is a static check on what the mod CAN call. It does not prove safety — a mod can still be subtly malicious. Treat as one signal among others.");
        }
        else
        {
            var dangers = report.CountOf(ModScanner.Severity.Danger);
            var warnings = report.CountOf(ModScanner.Severity.Warning);
            var infos = report.CountOf(ModScanner.Severity.Info);
            AddInspectHeader(body, "Summary");
            if (dangers > 0)
                AddScanLine(body, $"  ⛔ {dangers} danger{(dangers == 1 ? "" : "s")}", new Color(0.95f, 0.45f, 0.45f));
            if (warnings > 0)
                AddScanLine(body, $"  ⚠ {warnings} warning{(warnings == 1 ? "" : "s")}", new Color(1f, 0.78f, 0.25f));
            if (infos > 0)
                AddScanLine(body, $"  ℹ {infos} note{(infos == 1 ? "" : "s")}", new Color(0.7f, 0.85f, 0.95f));
            AddInspectMuted(body, $"Scanned {report.MethodsScanned} methods.");
        }

        // Group + render findings
        AddScanGroup(body, report, ModScanner.Severity.Danger,  "Dangers",  new Color(0.95f, 0.45f, 0.45f));
        AddScanGroup(body, report, ModScanner.Severity.Warning, "Warnings", new Color(1f, 0.78f, 0.25f));
        AddScanGroup(body, report, ModScanner.Severity.Info,    "Notes",    new Color(0.7f, 0.85f, 0.95f));

        if (report.ScannedSuccessfully && report.ScanErrors.Count > 0)
        {
            AddInspectHeader(body, "Scan errors (partial results)");
            foreach (var err in report.ScanErrors)
                AddInspectMuted(body, err);
        }

        var closeBtn = new Button
        {
            Text = "Close",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(220f, Math.Max(GetReferenceButtonHeight() * 0.85f, 44f)),
        };
        ApplyButtonTheme(closeBtn);
        closeBtn.Pressed += () => canvasLayer.QueueFree();
        panel.AddChild(closeBtn);

        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            canvasLayer.QueueFree();
            overlay.AcceptEvent();
        };

        closeBtn.CallDeferred("grab_focus");
    }

    private static void AddScanGroup(VBoxContainer body, ModScanner.Report report, ModScanner.Severity sev, string headerText, Color color)
    {
        var hits = report.Findings.Where(f => f.Sev == sev).ToList();
        if (hits.Count == 0) return;

        AddInspectHeader(body, $"{headerText} ({hits.Count})");

        // Sub-group by Category so duplicate API hits roll up
        foreach (var grp in hits.GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Category header line
            AddScanLine(body, $"  • {grp.Key} — {grp.First().Note}", color);

            // First few call sites under each category (cap to keep panel readable)
            const int callSiteCap = 6;
            var sites = grp.Select(f => $"      {f.ApiCalled}  ←  {f.CallSite}").Distinct().ToList();
            foreach (var site in sites.Take(callSiteCap))
                AddInspectMuted(body, site);
            if (sites.Count > callSiteCap)
                AddInspectMuted(body, $"      … and {sites.Count - callSiteCap} more call site{(sites.Count - callSiteCap == 1 ? "" : "s")}");
        }
    }

    private static void AddScanLine(VBoxContainer body, string text, Color color)
    {
        var lbl = new Label
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        ApplyFont(lbl, Math.Max(_buttonFontSize, 14));
        lbl.AddThemeColorOverride("font_color", color);
        body.AddChild(lbl);
    }
}
