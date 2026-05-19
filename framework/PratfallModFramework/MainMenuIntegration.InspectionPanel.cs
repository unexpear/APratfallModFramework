using Godot;

namespace PratfallModFramework;

// Read-only mod metadata panel — fired from the ℹ button on a mod card. Shows
// the manifest summary, every file in the mod folder with size + SHA-256, and
// the declared [ModPatch] targets if the mod is currently loaded. No side
// effects — just surfaces facts so the user can vet what a mod claims to be.
// CanvasLayer 130 so it sits above the Mods dialog at 128.
//
// Pure-info distinction: the consent-action counterpart is ShowScanPanel
// (🔍 button), which runs the IL safety scanner and marks the user-check gate.
public static partial class MainMenuIntegration
{
    public static void ShowInspectionPanel(SceneTree tree, ModInspector.Report report)
    {
        if (tree?.Root == null || report == null) return;
        var existing = tree.Root.GetNodeOrNull("ModFrameworkInspectLayer");
        if (existing != null) existing.QueueFree();

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkInspectLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkInspectDialog", MouseFilter = Control.MouseFilterEnum.Stop };
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
            Text = $"Inspecting: {report.Manifest?.Name ?? report.ModId}",
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

        // Manifest section
        AddInspectHeader(body, "Manifest");
        if (report.Manifest is { } m)
        {
            AddInspectKV(body, "id", m.Id);
            AddInspectKV(body, "version", m.Version);
            if (!string.IsNullOrWhiteSpace(m.Author)) AddInspectKV(body, "author", m.Author);
            if (!string.IsNullOrWhiteSpace(m.Description)) AddInspectKV(body, "description", m.Description);
            AddInspectKV(body, "multiplayer mode", m.EffectiveMode);
            if (!string.IsNullOrWhiteSpace(m.PinnedSha256)) AddInspectKV(body, "pinned sha256", m.PinnedSha256);
            if (!string.IsNullOrWhiteSpace(m.PckFile)) AddInspectKV(body, "pck file", m.PckFile);
            if (m.Requires.Count > 0) AddInspectKV(body, "requires", string.Join(", ", m.Requires));
            if (m.ConflictsWith.Count > 0) AddInspectKV(body, "conflicts with", string.Join(", ", m.ConflictsWith));
            // Source attribution — Workshop mods get a distinct line. WorkshopId
            // is the Steam published-file ID; users can paste it into a Steam
            // Workshop URL (steamcommunity.com/sharedfiles/filedetails/?id=<id>)
            // to view the mod's Workshop page.
            if (m.IsSteamWorkshopMod)
            {
                AddInspectKV(body, "source", m.WorkshopId != 0
                    ? $"📦 Steam Workshop (id {m.WorkshopId})"
                    : "📦 Steam Workshop");
            }
            else
            {
                AddInspectKV(body, "source", "local install");
            }
        }
        if (!string.IsNullOrEmpty(report.FolderPath)) AddInspectKV(body, "folder", report.FolderPath);

        // Files section
        AddInspectHeader(body, $"Files ({report.Files.Count})");
        if (report.Files.Count == 0)
            AddInspectMuted(body, "No files in mod folder.");
        else
        {
            foreach (var f in report.Files)
            {
                var sizeStr = f.ByteSize >= 1024 * 1024 ? $"{f.ByteSize / (1024.0 * 1024.0):0.00} MB"
                            : f.ByteSize >= 1024 ? $"{f.ByteSize / 1024.0:0.0} KB"
                            : $"{f.ByteSize} B";
                var line = new Label
                {
                    Text = $"  {f.FileName,-32} {sizeStr,12}    {f.Sha256Hex[..16]}…",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = $"{f.FileName}\nSize: {f.ByteSize} bytes\nSHA-256: {f.Sha256Hex}",
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                ApplyFont(line, Math.Max(_buttonFontSize - 1, 13));
                line.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.95f));
                body.AddChild(line);
            }
        }

        // Patches section
        AddInspectHeader(body, $"Declared Harmony patches ({report.DeclaredPatches.Count})");
        if (!report.PatchesAreFromLoadedAssembly)
        {
            AddInspectMuted(body, report.LoadStateNote ?? "Mod is not currently loaded — patches not inspected.");
        }
        else if (report.DeclaredPatches.Count == 0)
        {
            AddInspectMuted(body, "No [ModPatch] declarations found in this mod.");
        }
        else
        {
            foreach (var p in report.DeclaredPatches)
            {
                var line = new Label
                {
                    Text = $"  {p.PatchType,-10} {p.TargetTypeFullName}.{p.TargetMethod}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = $"Declared in: {p.DeclaringTypeFullName}",
                    MouseFilter = Control.MouseFilterEnum.Stop,
                };
                ApplyFont(line, Math.Max(_buttonFontSize - 1, 13));
                line.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.95f));
                body.AddChild(line);
            }
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
}
