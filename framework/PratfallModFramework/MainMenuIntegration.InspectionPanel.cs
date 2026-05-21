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

        // Preview image (Workshop thumbnail convention — Preview.png/.jpg/.jpeg
        // at the top of the mod folder, same as SteamWorkshopUploader.exe uses).
        // Renders inline at the top of the inspector if the mod ships one.
        TryAddPreviewImage(body, report.PreviewImagePath);

        // Manifest section
        AddInspectHeader(body, "Manifest");
        if (report.Manifest is { } manifest)
        {
            AddInspectKV(body, "id", manifest.Id);
            AddInspectKV(body, "version", manifest.Version);
            if (!string.IsNullOrWhiteSpace(manifest.Author)) AddInspectKV(body, "author", manifest.Author);
            if (!string.IsNullOrWhiteSpace(manifest.Description)) AddInspectKV(body, "description", manifest.Description);
            AddInspectKV(body, "multiplayer mode", manifest.EffectiveMode);
            if (!string.IsNullOrWhiteSpace(manifest.PinnedSha256)) AddInspectKV(body, "pinned sha256", manifest.PinnedSha256);
            if (!string.IsNullOrWhiteSpace(manifest.PckFile)) AddInspectKV(body, "pck file", manifest.PckFile);
            if (manifest.Requires.Count > 0) AddInspectKV(body, "requires", string.Join(", ", manifest.Requires));
            if (manifest.ConflictsWith.Count > 0) AddInspectKV(body, "conflicts with", string.Join(", ", manifest.ConflictsWith));
            // Source attribution — Workshop mods get a distinct line. WorkshopId
            // is the Steam published-file ID; users can paste it into a Steam
            // Workshop URL (steamcommunity.com/sharedfiles/filedetails/?id=<id>)
            // to view the mod's Workshop page.
            if (manifest.IsSteamWorkshopMod)
            {
                AddInspectKV(body, "source", manifest.WorkshopId != 0
                    ? $"📦 Steam Workshop (id {manifest.WorkshopId})"
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
            foreach (var file in report.Files)
            {
                var sizeStr = file.ByteSize >= 1024 * 1024 ? $"{file.ByteSize / (1024.0 * 1024.0):0.00} MB"
                            : file.ByteSize >= 1024 ? $"{file.ByteSize / 1024.0:0.0} KB"
                            : $"{file.ByteSize} B";
                AddInspectTooltipLine(body,
                    $"  {file.FileName,-32} {sizeStr,12}    {file.Sha256Hex[..16]}…",
                    $"{file.FileName}\nSize: {file.ByteSize} bytes\nSHA-256: {file.Sha256Hex}");
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
            foreach (var patch in report.DeclaredPatches)
            {
                AddInspectTooltipLine(body,
                    $"  {patch.PatchType,-10} {patch.TargetTypeFullName}.{patch.TargetMethod}",
                    $"Declared in: {patch.DeclaringTypeFullName}");
            }
        }

        var closeBtn = CreateAuxDialogCloseButton(panel, canvasLayer);

        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            canvasLayer.QueueFree();
            overlay.AcceptEvent();
        };

        closeBtn.CallDeferred("grab_focus");
    }

    // Loads the mod's Preview.png/.jpg/.jpeg and renders it at the top of the
    // inspector body as a centered TextureRect with preserved aspect, capped
    // at a sensible inline size so it doesn't dominate the panel. Silently
    // skips on any decode/IO error (the inspector still shows the rest of
    // the report — a missing preview shouldn't break inspection).
    private static void TryAddPreviewImage(VBoxContainer body, string? previewImagePath)
    {
        if (string.IsNullOrEmpty(previewImagePath) || !System.IO.File.Exists(previewImagePath))
            return;

        try
        {
            var image = new Image();
            var err = image.Load(previewImagePath);
            if (err != Error.Ok)
            {
                GD.PrintErr($"[ModFramework] InspectionPanel: preview image at {previewImagePath} failed to load (Godot error {err})");
                return;
            }
            var texture = ImageTexture.CreateFromImage(image);
            if (texture == null) return;

            // Center within a row, cap display size around 220px tall so a
            // 600×600 preview doesn't push the manifest section off-screen.
            // KeepAspectCentered preserves whatever aspect the author chose.
            var holder = new CenterContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            var rect = new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.FitWidthProportional,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                CustomMinimumSize = new Vector2(0, 220),
                TooltipText = "Workshop preview image (Preview.png/.jpg next to manifest.json)",
                MouseFilter = Control.MouseFilterEnum.Stop,
            };
            holder.AddChild(rect);
            body.AddChild(holder);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[ModFramework] InspectionPanel: preview-image render failed for {previewImagePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
