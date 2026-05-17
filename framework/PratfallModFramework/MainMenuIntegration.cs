using Godot;

namespace PratfallModFramework;

// Split across partial files for readability — see MainMenuIntegration.<DialogName>.cs.
// All partials share the private state declared below; shared style helpers
// (CreateFallbackDialogHost, ApplyButtonTheme, AddInspect*, etc.) live in this file.
public static partial class MainMenuIntegration
{
    private static SceneTree? _tree;
    private static bool _installed;
    // Toggle widgets currently in the open Mods dialog, keyed by mod id. Lets the
    // framework push visual state into the dialog when something other than the user
    // (e.g. the conflict-resolution prompt) flips a mod off.
    private static readonly Dictionary<string, ToggleSwitch> _dialogToggles = new(StringComparer.OrdinalIgnoreCase);
    private static Theme? _buttonTheme;
    private static Font? _buttonFont;
    private static int _buttonFontSize;
    private static StyleBox? _harvestedDialogStylebox;
    private static StyleBox? _harvestedCardStylebox;
    private static StyleBox? _harvestedFrameTextureStylebox;
    private static bool _themeDumpDone;
    private static bool _frameDiagnosticsDone;
    private static bool _everInjected;
    private static int _injectAttempts;
    private static bool _liveTreeDumped;
    private static Action? _onModsButtonPressed;
    private static Action? _onApplySelectedMods;
    private static Func<List<ModManifest>>? _getMods;
    private static Func<string, bool>? _isModEnabled;
    private static Action<string, bool>? _onToggleMod;
    // Returns null when the mod has no compatibility issues, otherwise a short
    // human-readable summary suitable for a tooltip ("conflicts with X; missing dep Y").
    private static Func<string, string?>? _getModIssueTooltip;
    // Read-only metadata view (manifest fields, file listing, declared patches). No side effects.
    private static Func<string, ModInspector.Report?>? _inspectMod;
    // Static IL safety scan (Cecil-backed). Side effect: marks the mod's fingerprint as
    // user-checked, since running the scan IS the user-consent action.
    private static Func<string, ModScanner.Report?>? _scanMod;

    public static void Install(SceneTree tree,
        Action? onModsPressed = null,
        Action? onApplySelectedMods = null,
        Func<List<ModManifest>>? getMods = null,
        Func<string, bool>? isModEnabled = null,
        Action<string, bool>? onToggleMod = null,
        Func<string, string?>? getModIssueTooltip = null,
        Func<string, ModInspector.Report?>? inspectMod = null,
        Func<string, ModScanner.Report?>? scanMod = null)
    {
        _tree = tree;
        _onModsButtonPressed = onModsPressed;
        _onApplySelectedMods = onApplySelectedMods;
        _getMods = getMods;
        _isModEnabled = isModEnabled;
        _onToggleMod = onToggleMod;
        _getModIssueTooltip = getModIssueTooltip;
        _inspectMod = inspectMod;
        _scanMod = scanMod;
        if (_installed) return;
        _installed = true;
        GD.Print("[ModFramework] MainMenuIntegration installed, waiting for main menu...");
    }

    public static bool TryInject()
    {
        if (_tree == null) return false;
        var root = _tree.Root;

        // Find the menu node + buttons container. Preferred path: locate the
        // MainMenuUIViewController instance and read HostButton.GetParent() —
        // HostButton is a [Export] field set from the .tscn, so its parent IS the
        // buttons container, regardless of how the .tscn names things. Fallbacks
        // remain for legacy builds and edge cases.
        Node? menuUi = null;
        Node? container = null;

        var controllers = FindNodesOfType<global::MainMenuUIViewController>(root);
        if (controllers.Count > 0)
        {
            menuUi = controllers[0];
            var ctrl = controllers[0];
            if (ctrl.HostButton != null && Godot.GodotObject.IsInstanceValid(ctrl.HostButton))
                container = ctrl.HostButton.GetParent();
        }

        if (container == null)
        {
            menuUi ??= root.GetNodeOrNull("MainMenuUI") ?? FindNodeByName(root, "MainMenuUI");
            container = menuUi != null
                ? (FindNodeByName(menuUi, "ButtonsContainer") ?? FindButtonContainer(menuUi) ?? FindButtonContainer(root))
                : FindButtonContainer(root);
        }

        if (container == null)
        {
            _injectAttempts++;
            // Wait until 30 polls (~15s) so the main menu has definitely loaded
            // (preloader, Steam login, etc. take ~10s on this hardware).
            if (!_everInjected && !_liveTreeDumped && _injectAttempts >= 30)
            {
                _liveTreeDumped = true;
                GD.Print($"[ModFramework] Live menu inject failed after {_injectAttempts} polls. menuUi found = {menuUi != null} ({menuUi?.GetType().Name ?? "null"})");
                if (menuUi != null)
                {
                    GD.Print("[ModFramework] Menu controller children:");
                    DumpTree(menuUi, 1, menuUi.Name);
                }
                else
                {
                    GD.Print("[ModFramework] All buttons in tree:");
                    DumpButtonsRecursive(root, 0);
                }
            }
            return false;
        }

        // Pick a style donor — controller's HostButton if available (most reliable),
        // else any existing Button in the container.
        Button? styleDonor = null;
        global::MainMenuUIViewController? ctrlForFields = controllers.Count > 0 ? controllers[0] : null;
        if (ctrlForFields?.HostButton != null && Godot.GodotObject.IsInstanceValid(ctrlForFields.HostButton))
            styleDonor = ctrlForFields.HostButton;
        else
            foreach (var c in container.GetChildren())
                if (c is Button b) { styleDonor = b; break; }

        if (styleDonor == null) return false;

        // Don't double-inject.
        foreach (var c in container.GetChildren())
            if (c is Button b && b.Name == "ModFrameworkModsButton") return true;

        // Insert position: prefer right after the (hidden) native ModButton if present,
        // else at the end. Hidden-and-still-in-tree means the visual order stays consistent.
        int insertIdx = container.GetChildren().Count;
        if (ctrlForFields?.ModButton != null && Godot.GodotObject.IsInstanceValid(ctrlForFields.ModButton))
        {
            var idx = container.GetChildren().ToList().IndexOf(ctrlForFields.ModButton);
            if (idx >= 0) insertIdx = idx + 1;
        }

        var modsBtn = new Button
        {
            Name = "ModFrameworkModsButton",
            Text = "  Mods  ",
            SizeFlagsHorizontal = styleDonor.SizeFlagsHorizontal,
            SizeFlagsVertical = styleDonor.SizeFlagsVertical,
        };
        modsBtn.Theme = styleDonor.Theme;
        _buttonTheme = styleDonor.Theme;
        _buttonFont = styleDonor.GetThemeFont("font");
        _buttonFontSize = styleDonor.GetThemeFontSize("font_size");
        modsBtn.AddThemeStyleboxOverride("normal", styleDonor.GetThemeStylebox("normal"));
        modsBtn.AddThemeStyleboxOverride("hover", styleDonor.GetThemeStylebox("hover"));
        modsBtn.AddThemeStyleboxOverride("pressed", styleDonor.GetThemeStylebox("pressed"));
        modsBtn.AddThemeFontOverride("font", _buttonFont);
        modsBtn.AddThemeFontSizeOverride("font_size", _buttonFontSize);

        modsBtn.Pressed += OnModsButtonPressed;

        container.AddChild(modsBtn);
        container.MoveChild(modsBtn, insertIdx);

        // Expand parent containers to fit the new button
        ExpandContainer(container);

        _everInjected = true;
        GD.Print("[ModFramework] Mods button added to main menu");
        return true;
    }

    private static void AddInspectHeader(VBoxContainer body, string text)
    {
        var lbl = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ApplyFont(lbl, Math.Max(_buttonFontSize + 1, 17));
        lbl.AddThemeColorOverride("font_color", new Color(0.97f, 0.92f, 0.71f));
        body.AddChild(lbl);
    }

    private static void AddInspectKV(VBoxContainer body, string key, string value)
    {
        var lbl = new Label
        {
            Text = $"  {key}: {value}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        ApplyFont(lbl, Math.Max(_buttonFontSize - 1, 13));
        lbl.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.95f));
        body.AddChild(lbl);
    }

    private static void AddInspectMuted(VBoxContainer body, string text)
    {
        var lbl = new Label
        {
            Text = $"  {text}",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        ApplyFont(lbl, Math.Max(_buttonFontSize - 1, 13));
        lbl.AddThemeColorOverride("font_color", new Color(0.7f, 0.78f, 0.82f));
        body.AddChild(lbl);
    }

    private static VBoxContainer CreateFallbackDialogHost(Control overlay, Vector2 dialogSize, bool compact = false)
    {
        // Light scrim like Options — the game world stays visible behind the dialog.
        var scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.05f, 0.07f, 0.4f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        SetFullRect(scrim);
        overlay.AddChild(scrim);

        var centering = new CenterContainer
        {
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        SetFullRect(centering);
        overlay.AddChild(centering);

        // The Mods dialog wants a tall content area for its mod list. Compact callers
        // (conflict prompt, etc.) skip that floor so the panel hugs its content instead
        // of stretching to a tall empty box.
        var contentWidth = Math.Max(compact ? 380f : 500f, dialogSize.X - Math.Max(dialogSize.X * 0.1f, 72f));
        var contentHeightFloor = compact ? 0f : 340f;
        var contentHeight = Math.Max(contentHeightFloor, dialogSize.Y - Math.Max(dialogSize.Y * 0.14f, 92f));

        HarvestNativeStyleboxes();
        HarvestNativeFrameTextures();

        var shell = new PanelContainer
        {
            CustomMinimumSize = compact ? new Vector2(dialogSize.X, 0) : dialogSize,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        // Priority: rocky-texture frame harvested from DialogUI > harvested theme texture
        // stylebox > our flat fallback. Stops short of fully reconstructing DialogUI's
        // scene composition, but uses its actual body texture so the dialog reads as a
        // Pratfall native element rather than a generic Godot panel.
        var shellStyle = _harvestedFrameTextureStylebox
            ?? (_harvestedDialogStylebox is StyleBoxTexture ? _harvestedDialogStylebox : CreateDialogPanelStyle());
        shell.AddThemeStyleboxOverride("panel", shellStyle);
        centering.AddChild(shell);

        var root = new MarginContainer();
        root.AddThemeConstantOverride("margin_left", 24);
        root.AddThemeConstantOverride("margin_top", 22);
        root.AddThemeConstantOverride("margin_right", 24);
        root.AddThemeConstantOverride("margin_bottom", 20);
        shell.AddChild(root);

        var panel = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = compact ? Control.SizeFlags.ShrinkBegin : Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(contentWidth, contentHeight),
        };
        panel.AddThemeConstantOverride("separation", 14);
        root.AddChild(panel);
        return panel;
    }

    private static void ExpandContainer(Node container)
    {
        // Walk up and expand Rect2 / min_size to fit the new button
        var parent = container.GetParent();
        while (parent != null && parent != _tree?.Root)
        {
            if (parent is Control control)
            {
                var h = control.Size.Y + 40;
                control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, h);
            }
            parent = parent.GetParent();
        }
    }

    private static Node? FindButtonContainer(Node root)
    {
        // Search for containers that have buttons with text "Host", "Offline", "Options"
        var containers = FindNodesOfType<VBoxContainer>(root);
        foreach (var c in containers)
        {
            bool hasHost = FindChildByText(c, "Host Game") != null || FindChildByText(c, "Host") != null;
            bool hasOffline = FindChildByText(c, "Play Offline") != null || FindChildByText(c, "Offline") != null;
            bool hasOptions = FindChildByText(c, "Options") != null;
            if (hasHost && hasOffline && hasOptions)
                return c;
        }
        var hcontainers = FindNodesOfType<HBoxContainer>(root);
        foreach (var c in hcontainers)
        {
            bool hasHost = FindChildByText(c, "Host Game") != null || FindChildByText(c, "Host") != null;
            bool hasOffline = FindChildByText(c, "Play Offline") != null || FindChildByText(c, "Offline") != null;
            bool hasOptions = FindChildByText(c, "Options") != null;
            if (hasHost && hasOffline && hasOptions)
                return c;
        }
        return null;
    }

    private static void DumpButtonsRecursive(Node node, int depth)
    {
        if (node is Button btn)
        {
            var indent = new string(' ', depth * 2);
            GD.Print($"[ModFramework] {indent}Button name='{btn.Name}' text='{btn.Text}' parent='{btn.GetParent()?.Name}' ({btn.GetParent()?.GetType().Name})");
        }
        foreach (var child in node.GetChildren())
            DumpButtonsRecursive(child, depth + 1);
    }

    private static Button? FindChildByText(Node parent, string text)
    {
        foreach (var child in parent.GetChildren())
        {
            if (child is Button btn && btn.Text.Trim() == text)
                return btn;
            var found = FindChildByText(child, text);
            if (found != null) return found;
        }
        return null;
    }

    private static Node? FindNodeByName(Node root, string name)
    {
        if (root.Name == name) return root;
        foreach (var child in root.GetChildren())
        {
            var found = FindNodeByName(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private static List<T> FindNodesOfType<T>(Node root) where T : Node
    {
        var results = new List<T>();
        if (root is T t) results.Add(t);
        foreach (var child in root.GetChildren())
            results.AddRange(FindNodesOfType<T>(child));
        return results;
    }

    private static void DumpTree(Node node, int depth, string label)
    {
        int btnCount = 0;
        foreach (var c in node.GetChildren())
        {
            string info;
            if (c is Button b) { info = $"Button[\"{b.Text}\"]"; btnCount++; }
            else if (c is Label l) info = $"Label[\"{l.Text}\"]";
            else if (c is Panel p) info = "Panel";
            else if (c is ColorRect cr) info = "ColorRect";
            else if (c is VBoxContainer) info = "VBoxContainer";
            else if (c is HBoxContainer) info = "HBoxContainer";
            else if (c is GridContainer) info = "GridContainer";
            else if (c is Control) info = "Control";
            else if (c is CanvasLayer) info = "CanvasLayer";
            else info = c.GetType().Name;
            GD.Print($"[ModFramework] {new string(' ', depth * 2)}{c.Name} ({info})");
            DumpTree(c, depth + 1, "");
        }
        if (depth == 0 && label == "root")
            GD.Print($"[ModFramework] Total buttons in tree: {btnCount}");
    }

    private static void SetFullRect(Control control)
    {
        control.AnchorLeft = 0;
        control.AnchorTop = 0;
        control.AnchorRight = 1;
        control.AnchorBottom = 1;
        control.OffsetLeft = 0;
        control.OffsetTop = 0;
        control.OffsetRight = 0;
        control.OffsetBottom = 0;
    }

    private static Vector2 GetDialogSize()
    {
        var viewportSize = _tree?.Root.GetViewport().GetVisibleRect().Size ?? new Vector2(1366, 768);
        if (TryGetNativeDialogFrameSize(out var nativeDialogSize))
        {
            var maxWidth = viewportSize.X * 0.56f;
            var minWidth = Math.Min(nativeDialogSize.X, maxWidth);
            var targetWidth = Mathf.Clamp(nativeDialogSize.X * 1.55f, minWidth, maxWidth);
            var maxHeight = viewportSize.Y * 0.76f;
            var minHeight = Math.Min(nativeDialogSize.Y, maxHeight);
            var targetHeight = Mathf.Clamp(nativeDialogSize.Y * 1.9f, minHeight, maxHeight);
            return new Vector2(targetWidth, targetHeight);
        }

        var fallbackWidth = Mathf.Clamp(viewportSize.X * 0.48f, 620f, 920f);
        var fallbackHeight = Mathf.Clamp(viewportSize.Y * 0.72f, 500f, 720f);
        return new Vector2(fallbackWidth, fallbackHeight);
    }

    private static bool TryGetNativeDialogFrameSize(out Vector2 dialogFrameSize)
    {
        dialogFrameSize = Vector2.Zero;
        if (_tree == null)
            return false;

        var dialogUi = FindNodeByName(_tree.Root, "DialogUI");
        if (dialogUi == null)
            return false;

        if (FindNodeByName(dialogUi, "HBoxContainer") is not Control frame)
            return false;

        dialogFrameSize = frame.Size;
        if (dialogFrameSize.X > 1f && dialogFrameSize.Y > 1f)
            return true;

        dialogFrameSize = frame.GetCombinedMinimumSize();
        if (dialogFrameSize.X > 1f && dialogFrameSize.Y > 1f)
            return true;

        dialogFrameSize = frame.CustomMinimumSize;
        return dialogFrameSize.X > 1f && dialogFrameSize.Y > 1f;
    }

    private static float GetReferenceButtonHeight()
    {
        if (_tree == null)
            return 52f;

        var mainMenuUi = _tree.Root.GetNodeOrNull("MainMenuUI")
            ?? FindNodeByName(_tree.Root, "MainMenuUI");
        if (mainMenuUi == null)
            return 52f;

        var optionsBtn = FindChildByText(mainMenuUi, "Options");
        if (optionsBtn == null)
            return 52f;

        if (optionsBtn.Size.Y > 1f)
            return optionsBtn.Size.Y;

        var minimumSize = optionsBtn.GetCombinedMinimumSize();
        return minimumSize.Y > 1f ? minimumSize.Y : 52f;
    }

    private static void WireVerticalFocus(IReadOnlyList<Control> controls)
    {
        for (var i = 0; i < controls.Count; i++)
        {
            var current = controls[i];
            if (i > 0)
            {
                var previous = controls[i - 1];
                current.FocusPrevious = current.GetPathTo(previous);
                current.SetFocusNeighbor(Side.Top, current.GetPathTo(previous));
            }

            if (i < controls.Count - 1)
            {
                var next = controls[i + 1];
                current.FocusNext = current.GetPathTo(next);
                current.SetFocusNeighbor(Side.Bottom, current.GetPathTo(next));
            }
        }
    }

    private static bool IsActionPressed(InputEvent @event, string actionName)
    {
        return @event is InputEventAction actionEvent
            && actionEvent.Pressed
            && actionEvent.Action == actionName;
    }

    private static void ApplyFont(Control control, int fontSize)
    {
        if (_buttonFont != null)
            control.AddThemeFontOverride("font", _buttonFont);

        control.AddThemeFontSizeOverride("font_size", fontSize);
    }

    private static void ApplyButtonTheme(Button button)
    {
        if (_buttonTheme != null)
            button.Theme = _buttonTheme;

        var normal = FindThemeStylebox("normal", "Button");
        if (normal != null)
            button.AddThemeStyleboxOverride("normal", normal);

        var hover = FindThemeStylebox("hover", "Button") ?? normal;
        if (hover != null)
            button.AddThemeStyleboxOverride("hover", hover);

        var pressed = FindThemeStylebox("pressed", "Button") ?? normal;
        if (pressed != null)
            button.AddThemeStyleboxOverride("pressed", pressed);

        ApplyFont(button, Math.Max(_buttonFontSize, 16));
    }

    private static StyleBox? FindThemeStylebox(string name, string themeType)
    {
        if (_buttonTheme == null)
            return null;

        return _buttonTheme.HasStylebox(name, themeType)
            ? _buttonTheme.GetStylebox(name, themeType)
            : null;
    }

    private static StyleBoxFlat CreateDialogPanelStyle()
    {
        // Match Pratfall's Options dialog: medium-dark teal fill with a near-black hairline
        // edge instead of a prominent stroke. The rocky bumps on Options are scene-level
        // TextureRects we can't reproduce with StyleBoxFlat — this color/edge tuning gets
        // as close as a flat style can get.
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.13f, 0.26f, 0.31f, 0.98f),
            BorderColor = new Color(0.04f, 0.08f, 0.10f),
            ShadowColor = new Color(0f, 0f, 0f, 0.5f),
            ShadowSize = 16,
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(24);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat CreateCardStyle()
    {
        // Cards sit on top of the dialog panel and need to read LIGHTER than the panel,
        // matching the Options group-card pattern (light-on-dark, not the inverse).
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.21f, 0.38f, 0.44f, 1.0f),
            BorderColor = new Color(0.30f, 0.50f, 0.58f, 0.4f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(14);
        style.SetContentMarginAll(0);
        return style;
    }

    // Walk DialogUI in the live tree and pull out the rocky frame Texture2D the game
    // composes its dialogs from. We log every textured node first (so we can refine the
    // pick) and then build a StyleBoxTexture from the most likely body texture.
    private static void HarvestNativeFrameTextures()
    {
        if (_tree == null) return;
        if (_harvestedFrameTextureStylebox != null) return;

        var dialogUi = FindNodeByName(_tree.Root, "DialogUI");
        if (dialogUi == null) return;

        if (!_frameDiagnosticsDone)
        {
            _frameDiagnosticsDone = true;
            foreach (var tr in FindNodesOfType<TextureRect>(dialogUi))
            {
                if (tr.Texture == null) continue;
                var path = ComputeRelativePath(dialogUi, tr);
                var resPath = string.IsNullOrEmpty(tr.Texture.ResourcePath) ? tr.Texture.GetType().Name : tr.Texture.ResourcePath;
                var sz = tr.Texture.GetSize();
                GD.Print($"[ModFramework] DialogUI TextureRect: path={path} size={sz.X:0}x{sz.Y:0} res={resPath}");
            }
        }

        // The dialog body texture lives at:
        //   DialogUI > RootCanvasLayer > Root > HBoxContainer > MarginContainer > VBoxContainer > MarginContainer > TextureRect
        // Walk that path, falling back to the first textured TextureRect deep enough to be the body.
        var bodyTr = ResolveBodyTextureRect(dialogUi);
        if (bodyTr?.Texture == null) return;

        // Texture margins drive 9-slice. We don't know the actual design margins, so a
        // moderate value preserves rocky corner detail without over-stretching the body.
        var sb = new StyleBoxTexture
        {
            Texture = bodyTr.Texture,
            TextureMarginLeft = 48,
            TextureMarginTop = 48,
            TextureMarginRight = 48,
            TextureMarginBottom = 48,
        };
        _harvestedFrameTextureStylebox = sb;
        GD.Print($"[ModFramework] Harvested DialogUI body texture as StyleBoxTexture (size={bodyTr.Texture.GetSize().X:0}x{bodyTr.Texture.GetSize().Y:0})");
    }

    private static TextureRect? ResolveBodyTextureRect(Node dialogUi)
    {
        // Try the exact known path first.
        var direct = dialogUi.GetNodeOrNull("RootCanvasLayer/Root/HBoxContainer/MarginContainer/VBoxContainer/MarginContainer");
        if (direct != null)
        {
            foreach (var child in direct.GetChildren())
                if (child is TextureRect tr && tr.Texture != null) return tr;
        }

        // Fall back: pick the textured TextureRect with the largest area (likely the body).
        TextureRect? best = null;
        var bestArea = 0f;
        foreach (var tr in FindNodesOfType<TextureRect>(dialogUi))
        {
            if (tr.Texture == null) continue;
            var sz = tr.Texture.GetSize();
            var area = sz.X * sz.Y;
            if (area > bestArea) { bestArea = area; best = tr; }
        }
        return best;
    }

    private static string ComputeRelativePath(Node ancestor, Node node)
    {
        var parts = new List<string>();
        Node? cur = node;
        while (cur != null && cur != ancestor)
        {
            parts.Add(cur.Name);
            cur = cur.GetParent();
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    // Harvest styleboxes from the game's already-loaded UI theme so the dialog
    // visually matches Pratfall's native frame styling instead of looking like
    // a stock Godot debug panel. Runs on first dialog open and caches results.
    private static void HarvestNativeStyleboxes()
    {
        if (_tree == null) return;
        if (_harvestedDialogStylebox != null && _harvestedCardStylebox != null && _themeDumpDone) return;

        var menuNode = FindNodeByName(_tree.Root, "MainMenuUI");
        if (menuNode == null) return;
        var menuUi = menuNode as Control ?? FindFirstDescendantControl(menuNode);
        if (menuUi == null) return;

        if (_harvestedDialogStylebox == null)
            _harvestedDialogStylebox = TryFindStylebox(menuUi,
                names: new[] { "panel", "background", "frame", "main_panel" },
                types: new[] { "PanelContainer", "Panel", "Window", "AcceptDialog", "Popup", "PopupPanel", "PopupMenu" },
                label: "dialog");

        if (_harvestedCardStylebox == null)
            _harvestedCardStylebox = TryFindStylebox(menuUi,
                names: new[] { "tab_selected", "panel", "tab_disabled", "tab_unselected" },
                types: new[] { "TabContainer", "TabBar", "PanelContainer", "Panel" },
                label: "card");

        if (!_themeDumpDone)
        {
            _themeDumpDone = true;
            DumpReachableThemes(menuUi);
        }
    }

    private static StyleBox? TryFindStylebox(Control source, string[] names, string[] types, string label)
    {
        foreach (var type in types)
        {
            foreach (var name in names)
            {
                if (!source.HasThemeStylebox(name, type)) continue;
                var sb = source.GetThemeStylebox(name, type);
                if (sb == null) continue;
                GD.Print($"[ModFramework] Harvested {label} stylebox {type}/{name} -> {sb.GetType().Name}");
                return sb;
            }
        }
        return null;
    }

    private static Control? FindFirstDescendantControl(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is Control c) return c;
            var found = FindFirstDescendantControl(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void DumpReachableThemes(Control source)
    {
        Node? node = source;
        var seen = new HashSet<Theme>();
        while (node != null)
        {
            if (node is Control c && c.Theme != null && seen.Add(c.Theme))
            {
                GD.Print($"[ModFramework] --- Theme on '{node.Name}' ({c.Theme.GetType().Name}) ---");
                foreach (var type in c.Theme.GetTypeList())
                {
                    var boxes = c.Theme.GetStyleboxList(type);
                    if (boxes.Length == 0) continue;
                    foreach (var name in boxes)
                    {
                        var sb = c.Theme.GetStylebox(name, type);
                        GD.Print($"[ModFramework]   {type}/{name} -> {sb.GetType().Name}");
                    }
                }
            }
            node = node.GetParent();
        }
    }

}
