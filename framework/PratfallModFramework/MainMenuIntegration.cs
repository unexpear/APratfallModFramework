using Godot;

namespace PratfallModFramework;

public static class MainMenuIntegration
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
    private static bool _dumpedNotFound;
    private static Action? _onModsButtonPressed;
    private static Action? _onApplySelectedMods;
    private static Func<List<ModManifest>>? _getMods;
    private static Func<string, bool>? _isModEnabled;
    private static Action<string, bool>? _onToggleMod;
    // Returns null when the mod has no compatibility issues, otherwise a short
    // human-readable summary suitable for a tooltip ("conflicts with X; missing dep Y").
    private static Func<string, string?>? _getModIssueTooltip;
    // Trust panel callbacks — wired by ModManager.Initialize. When null, the Trust button
    // simply isn't rendered in the Mods dialog header.
    private static Action? _onOpenTrustPanel;

    public static void Install(SceneTree tree,
        Action? onModsPressed = null,
        Action? onApplySelectedMods = null,
        Func<List<ModManifest>>? getMods = null,
        Func<string, bool>? isModEnabled = null,
        Action<string, bool>? onToggleMod = null,
        Func<string, string?>? getModIssueTooltip = null,
        Action? onOpenTrustPanel = null)
    {
        _tree = tree;
        _onModsButtonPressed = onModsPressed;
        _onApplySelectedMods = onApplySelectedMods;
        _getMods = getMods;
        _isModEnabled = isModEnabled;
        _onToggleMod = onToggleMod;
        _getModIssueTooltip = getModIssueTooltip;
        _onOpenTrustPanel = onOpenTrustPanel;
        if (_installed) return;
        _installed = true;
        GD.Print("[ModFramework] MainMenuIntegration installed, waiting for main menu...");
    }

    public static bool TryInject()
    {
        if (_tree == null) return false;
        var root = _tree.Root;

        // Find the MainMenuUI node (actual UI, not the 3D scene root)
        Node? menuUi = root.GetNodeOrNull("MainMenuUI")
                    ?? FindNodeByName(root, "MainMenuUI");
        if (menuUi == null)
        {
            // Dump the tree once before the very first inject succeeds so the diagnostic
            // is available when something is genuinely wrong. After the first success the
            // menu may legitimately come and go (game enter/exit) — stay quiet then.
            if (!_everInjected && !_dumpedNotFound)
            {
                _dumpedNotFound = true;
                GD.Print("[ModFramework] MainMenuUI not found, dumping tree.");
                DumpTree(root, 0, "root");
            }
            return false;
        }

        // Find ButtonsContainer under MainMenuUI
        Node? container = FindNodeByName(menuUi, "ButtonsContainer")
            ?? FindButtonContainer(menuUi)
            ?? FindButtonContainer(root);
        if (container == null)
        {
            GD.Print("[ModFramework] Buttons container not found, dumping MainMenuUI tree:");
            DumpTree(menuUi, 1, "MainMenuUI");
            return false;
        }

        var optionsBtn = FindChildByText(container, "Options");
        var offlineBtn = FindChildByText(container, "Play Offline");
        if (optionsBtn == null || offlineBtn == null) return false;

        var existingModsBtn = FindChildByText(container, "Mods");
        if (existingModsBtn != null)
            return true;

        // Find index: insert mods button after Play Offline, before Options
        int offlineIdx = container.GetChildren().ToList().IndexOf(offlineBtn);
        int insertIdx = offlineIdx + 1;

        // Create mods button styled like the existing buttons
        var modsBtn = new Button
        {
            Text = "  Mods  ",
            SizeFlagsHorizontal = optionsBtn.SizeFlagsHorizontal,
            SizeFlagsVertical = optionsBtn.SizeFlagsVertical,
        };
        // Copy theme/style from one of the existing buttons
        modsBtn.Theme = optionsBtn.Theme;
        _buttonTheme = optionsBtn.Theme;
        _buttonFont = optionsBtn.GetThemeFont("font");
        _buttonFontSize = optionsBtn.GetThemeFontSize("font_size");
        modsBtn.AddThemeStyleboxOverride("normal", optionsBtn.GetThemeStylebox("normal"));
        modsBtn.AddThemeStyleboxOverride("hover", optionsBtn.GetThemeStylebox("hover"));
        modsBtn.AddThemeStyleboxOverride("pressed", optionsBtn.GetThemeStylebox("pressed"));
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

    private static void OnModsButtonPressed()
    {
        GD.Print("[ModFramework] Mods button pressed");
        _onModsButtonPressed?.Invoke();
        var existing = _tree?.Root.GetNodeOrNull("ModFrameworkDialogLayer");
        if (existing != null) { existing.QueueFree(); return; }
        if (_tree == null) return;

        // Wrap in a high-layer CanvasLayer so the dialog renders and receives input
        // above the main menu's own CanvasLayer-based UI.
        var canvasLayer = new CanvasLayer
        {
            Name = "ModFrameworkDialogLayer",
            Layer = 128,
        };
        _tree.Root.AddChild(canvasLayer);

        var overlay = new Control
        {
            Name = "ModFrameworkDialog",
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);
        overlay.GuiInput += (InputEvent @event) =>
        {
            if (!IsActionPressed(@event, "ui_cancel"))
                return;

            canvasLayer.QueueFree();
            overlay.AcceptEvent();
        };

        var dialogSize = GetDialogSize();
        var panel = CreateFallbackDialogHost(overlay, dialogSize);
        var focusables = new List<Control>();

        var header = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        panel.AddChild(header);

        var title = new Label
        {
            Text = "Installed Mods",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyFont(title, Math.Max(_buttonFontSize + 10, 24));
        title.AddThemeColorOverride("font_color", new Color(0.97f, 0.92f, 0.71f));
        header.AddChild(title);

        if (_onApplySelectedMods != null)
        {
            var applyButton = new Button
            {
                Text = "Load Enabled Mods",
                FocusMode = Control.FocusModeEnum.All,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
            };
            ApplyButtonTheme(applyButton);
            applyButton.CustomMinimumSize = new Vector2(220f, Math.Max(GetReferenceButtonHeight(), 48f));
            applyButton.Pressed += _onApplySelectedMods;
            header.AddChild(applyButton);
            focusables.Add(applyButton);
            // Note: no EnsureControlVisible — applyButton is outside the ScrollContainer.
        }

        if (_onOpenTrustPanel != null)
        {
            var trustButton = new Button
            {
                Text = "Trust",
                FocusMode = Control.FocusModeEnum.All,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                TooltipText = "Trust settings: Open / Trusted-only mode, manage trusted mod hashes, promote quarantined mods.",
            };
            ApplyButtonTheme(trustButton);
            trustButton.CustomMinimumSize = new Vector2(110f, Math.Max(GetReferenceButtonHeight(), 48f));
            trustButton.Pressed += _onOpenTrustPanel;
            header.AddChild(trustButton);
            focusables.Add(trustButton);
        }

        var mods = _getMods?.Invoke() ?? new List<ModManifest>();
        var countLabel = new Label
        {
            Text = mods.Count == 1 ? "1 mod" : $"{mods.Count} mods",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyFont(countLabel, Math.Max(_buttonFontSize, 14));
        countLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.9f, 0.93f));
        header.AddChild(countLabel);

        var subtitle = new Label
        {
            Text = "Manage installed mods for this session. Startup-loaded mods apply when you press Load Enabled Mods or start a game.",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        ApplyFont(subtitle, Math.Max(_buttonFontSize - 1, 14));
        subtitle.AddThemeColorOverride("font_color", new Color(0.79f, 0.89f, 0.91f));
        panel.AddChild(subtitle);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            FollowFocus = true,
            CustomMinimumSize = new Vector2(
                Math.Max(500f, dialogSize.X - Math.Max(dialogSize.X * 0.12f, 96f)),
                Math.Max(280f, dialogSize.Y - Math.Max(dialogSize.Y * 0.26f, 140f))),
        };
        panel.AddChild(scroll);

        var list = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        list.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(list);

        if (mods.Count == 0)
        {
            var emptyState = new PanelContainer
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            emptyState.AddThemeStyleboxOverride("panel",
                _harvestedCardStylebox is StyleBoxTexture ? _harvestedCardStylebox : CreateCardStyle());
            list.AddChild(emptyState);

            var emptyMargin = new MarginContainer();
            emptyMargin.AddThemeConstantOverride("margin_left", 18);
            emptyMargin.AddThemeConstantOverride("margin_top", 16);
            emptyMargin.AddThemeConstantOverride("margin_right", 18);
            emptyMargin.AddThemeConstantOverride("margin_bottom", 16);
            emptyState.AddChild(emptyMargin);

            var emptyLabel = new Label
            {
                Text = "No mods were found in the current install.",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            ApplyFont(emptyLabel, Math.Max(_buttonFontSize, 15));
            emptyLabel.AddThemeColorOverride("font_color", new Color(0.9f, 0.95f, 0.96f));
            emptyMargin.AddChild(emptyLabel);
        }
        else
        {
            foreach (var mod in mods)
            {
                var card = new PanelContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkBegin
                };
                card.AddThemeStyleboxOverride("panel",
                    _harvestedCardStylebox is StyleBoxTexture ? _harvestedCardStylebox : CreateCardStyle());
                list.AddChild(card);

                var cardMargin = new MarginContainer();
                cardMargin.AddThemeConstantOverride("margin_left", 16);
                cardMargin.AddThemeConstantOverride("margin_top", 14);
                cardMargin.AddThemeConstantOverride("margin_right", 16);
                cardMargin.AddThemeConstantOverride("margin_bottom", 14);
                card.AddChild(cardMargin);

                var cardBody = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                };
                cardBody.AddThemeConstantOverride("separation", 8);
                cardMargin.AddChild(cardBody);

                var topRow = new HBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                topRow.AddThemeConstantOverride("separation", 16);
                cardBody.AddChild(topRow);

                var infoColumn = new VBoxContainer
                {
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
                };
                infoColumn.AddThemeConstantOverride("separation", 3);
                topRow.AddChild(infoColumn);

                var titleRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                titleRow.AddThemeConstantOverride("separation", 8);
                infoColumn.AddChild(titleRow);

                var titleLabel = new Label
                {
                    Text = $"{mod.Name}  v{mod.Version}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                ApplyFont(titleLabel, Math.Max(_buttonFontSize + 2, 18));
                titleLabel.AddThemeColorOverride("font_color", new Color(0.95f, 0.97f, 0.98f));
                titleRow.AddChild(titleLabel);

                // Compatibility badge: if the cached compat report flags this mod, show a
                // ⚠ next to the name with the issue list as a hover tooltip. Snapshot at
                // dialog-open time — for live updates the dialog would need to subscribe.
                var issueTooltip = _getModIssueTooltip?.Invoke(mod.Id);
                if (!string.IsNullOrEmpty(issueTooltip))
                {
                    var badge = new Label
                    {
                        Text = "⚠",
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                        TooltipText = issueTooltip,
                        MouseFilter = Control.MouseFilterEnum.Stop, // needed for tooltip
                    };
                    ApplyFont(badge, Math.Max(_buttonFontSize + 4, 22));
                    badge.AddThemeColorOverride("font_color", new Color(1f, 0.78f, 0.25f));
                    titleRow.AddChild(badge);
                }

                var authorLabel = new Label
                {
                    Text = string.IsNullOrWhiteSpace(mod.Author) ? "author unknown" : $"by {mod.Author}",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                ApplyFont(authorLabel, Math.Max(_buttonFontSize - 1, 14));
                authorLabel.AddThemeColorOverride("font_color", new Color(0.76f, 0.88f, 0.92f));
                infoColumn.AddChild(authorLabel);

                var toggle = new ToggleSwitch(_isModEnabled?.Invoke(mod.Id) ?? false);
                var captured = mod.Id;
                _dialogToggles[captured] = toggle;
                toggle.Toggled += (pressed) => _onToggleMod?.Invoke(captured, pressed);
                toggle.TreeExited += () =>
                {
                    if (_dialogToggles.TryGetValue(captured, out var current) && current == toggle)
                        _dialogToggles.Remove(captured);
                };
                toggle.FocusEntered += () => scroll.EnsureControlVisible(toggle);
                focusables.Add(toggle);
                topRow.AddChild(toggle);

                if (!string.IsNullOrEmpty(mod.Description))
                {
                    cardBody.AddChild(new Label
                    {
                        Text = mod.Description,
                        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                        AutowrapMode = TextServer.AutowrapMode.WordSmart,
                    });
                    if (cardBody.GetChildCount() > 0 && cardBody.GetChild(cardBody.GetChildCount() - 1) is Label descLabel)
                    {
                        ApplyFont(descLabel, Math.Max(_buttonFontSize - 1, 14));
                        descLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.9f, 0.92f));
                    }
                }
            }
        }

        var closeBtn = new Button
        {
            Text = "Close",
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(
                Mathf.Clamp(dialogSize.X * 0.34f, 210f, 320f),
                Math.Max(GetReferenceButtonHeight(), 52f)),
        };
        ApplyButtonTheme(closeBtn);
        closeBtn.FocusMode = Control.FocusModeEnum.All;
        closeBtn.Pressed += () => canvasLayer.QueueFree();
        panel.AddChild(closeBtn);
        focusables.Add(closeBtn);
        WireVerticalFocus(focusables);
        (focusables.FirstOrDefault() ?? closeBtn).CallDeferred("grab_focus");
    }

    // Trust settings panel — toggle Open / Trusted-only mode, view/remove trusted
    // hashes, see quarantined mods waiting for trust, and promote them with one click.
    // Reuses the same compact dialog scaffolding as the conflict prompt. Layer 130 so
    // it sits above the Mods dialog at 128.
    public static void ShowTrustPanel(SceneTree tree,
        Func<ModTrustConfig> getTrust,
        Action<ModTrustConfig> saveTrust,
        Func<IReadOnlyList<ModManager.QuarantinedModInfo>> scanQuarantine,
        Func<ModManager.QuarantinedModInfo, string?> promoteQuarantine)
    {
        if (tree?.Root == null) return;
        var existing = tree.Root.GetNodeOrNull("ModFrameworkTrustLayer");
        if (existing != null) { existing.QueueFree(); return; }

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkTrustLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkTrustDialog", MouseFilter = Control.MouseFilterEnum.Stop };
        SetFullRect(overlay);
        canvasLayer.AddChild(overlay);

        _tree ??= tree;

        var viewportSize = tree.Root.GetViewport().GetVisibleRect().Size;
        var dialogSize = new Vector2(
            Mathf.Clamp(viewportSize.X * 0.50f, 540f, 760f),
            Mathf.Clamp(viewportSize.Y * 0.65f, 420f, 640f));
        var panel = CreateFallbackDialogHost(overlay, dialogSize, compact: false);

        // We rebuild the body on every change so the trusted-hash list and quarantine
        // list stay in sync without manually wiring per-row events.
        Action? rebuild = null;
        rebuild = () =>
        {
            foreach (var child in panel.GetChildren()) child.QueueFree();
            BuildTrustPanelBody(panel, dialogSize, getTrust, saveTrust, scanQuarantine, promoteQuarantine, rebuild!,
                close: () => canvasLayer.QueueFree());
        };
        rebuild();

        overlay.GuiInput += (InputEvent ev) =>
        {
            if (!IsActionPressed(ev, "ui_cancel")) return;
            canvasLayer.QueueFree();
            overlay.AcceptEvent();
        };
    }

    private static void BuildTrustPanelBody(VBoxContainer panel, Vector2 dialogSize,
        Func<ModTrustConfig> getTrust,
        Action<ModTrustConfig> saveTrust,
        Func<IReadOnlyList<ModManager.QuarantinedModInfo>> scanQuarantine,
        Func<ModManager.QuarantinedModInfo, string?> promoteQuarantine,
        Action rebuild,
        Action close)
    {
        var trust = getTrust();

        var title = new Label
        {
            Text = "Trust Settings",
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(title, Math.Max(_buttonFontSize + 8, 24));
        title.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        panel.AddChild(title);

        // Mode row — two pill buttons, the active one styled as the accent yellow.
        var modeRow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        modeRow.AddThemeConstantOverride("separation", 12);
        panel.AddChild(modeRow);

        var modeLabel = new Label { Text = "Mode:", VerticalAlignment = VerticalAlignment.Center };
        ApplyFont(modeLabel, Math.Max(_buttonFontSize, 16));
        modeRow.AddChild(modeLabel);

        var btnOpen = new Button { Text = trust.IsTrustedOnly ? "Open" : "● Open", FocusMode = Control.FocusModeEnum.All };
        ApplyButtonTheme(btnOpen);
        btnOpen.CustomMinimumSize = new Vector2(140, 40);
        btnOpen.Pressed += () => { trust.Mode = ModTrustConfig.ModeOpen; saveTrust(trust); rebuild(); };
        modeRow.AddChild(btnOpen);

        var btnTrusted = new Button { Text = trust.IsTrustedOnly ? "● Trusted-only" : "Trusted-only", FocusMode = Control.FocusModeEnum.All };
        ApplyButtonTheme(btnTrusted);
        btnTrusted.CustomMinimumSize = new Vector2(180, 40);
        btnTrusted.Pressed += () => { trust.Mode = ModTrustConfig.ModeTrustedOnly; saveTrust(trust); rebuild(); };
        modeRow.AddChild(btnTrusted);

        var modeNote = new Label
        {
            Text = trust.IsTrustedOnly
                ? "Trusted-only: incoming transfers with unknown hashes go to user://mods-quarantine/ until you trust them."
                : "Open: all received mods auto-install. Set Trusted-only if you only want vetted mods to activate.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        ApplyFont(modeNote, Math.Max(_buttonFontSize - 1, 13));
        modeNote.AddThemeColorOverride("font_color", new Color(0.78f, 0.88f, 0.92f));
        panel.AddChild(modeNote);

        // Trusted hashes — scrollable list with [×] remove on each row.
        var hashHeader = new Label { Text = $"Trusted hashes ({trust.TrustedSha256.Count})" };
        ApplyFont(hashHeader, Math.Max(_buttonFontSize, 16));
        hashHeader.AddThemeColorOverride("font_color", new Color(0.97f, 0.92f, 0.71f));
        panel.AddChild(hashHeader);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, Math.Max(dialogSize.Y * 0.40f, 180f)),
        };
        panel.AddChild(scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(list);

        if (trust.TrustedSha256.Count == 0)
        {
            var empty = new Label { Text = "No trusted hashes yet.", HorizontalAlignment = HorizontalAlignment.Center };
            ApplyFont(empty, Math.Max(_buttonFontSize - 1, 13));
            empty.AddThemeColorOverride("font_color", new Color(0.7f, 0.78f, 0.82f));
            list.AddChild(empty);
        }
        else
        {
            for (var i = 0; i < trust.TrustedSha256.Count; i++)
            {
                var hash = trust.TrustedSha256[i];
                var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                row.AddThemeConstantOverride("separation", 8);
                list.AddChild(row);

                var hashLabel = new Label { Text = hash.Length > 24 ? hash[..24] + "..." : hash, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = hash };
                ApplyFont(hashLabel, Math.Max(_buttonFontSize - 1, 13));
                hashLabel.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.95f));
                row.AddChild(hashLabel);

                var removeBtn = new Button { Text = "×", FocusMode = Control.FocusModeEnum.All };
                ApplyButtonTheme(removeBtn);
                removeBtn.CustomMinimumSize = new Vector2(40, 32);
                var captured = hash;
                removeBtn.Pressed += () =>
                {
                    trust.TrustedSha256.Remove(captured);
                    saveTrust(trust);
                    rebuild();
                };
                row.AddChild(removeBtn);
            }
        }

        // Quarantined mods — show with [Trust + Move] button per entry.
        var quarantined = scanQuarantine();
        var quarantineHeader = new Label { Text = $"Quarantined mods ({quarantined.Count})" };
        ApplyFont(quarantineHeader, Math.Max(_buttonFontSize, 16));
        quarantineHeader.AddThemeColorOverride("font_color", new Color(0.97f, 0.92f, 0.71f));
        panel.AddChild(quarantineHeader);

        if (quarantined.Count == 0)
        {
            var none = new Label { Text = "Nothing quarantined.", HorizontalAlignment = HorizontalAlignment.Center };
            ApplyFont(none, Math.Max(_buttonFontSize - 1, 13));
            none.AddThemeColorOverride("font_color", new Color(0.7f, 0.78f, 0.82f));
            panel.AddChild(none);
        }
        else
        {
            foreach (var q in quarantined)
            {
                var qrow = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                qrow.AddThemeConstantOverride("separation", 8);
                panel.AddChild(qrow);

                var qlabel = new Label
                {
                    Text = $"{q.ModId} ({q.ByteSize / 1024} KB)  {q.Sha256Hex[..16]}...",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    TooltipText = $"{q.FilePath}\nSHA-256: {q.Sha256Hex}",
                };
                ApplyFont(qlabel, Math.Max(_buttonFontSize - 1, 13));
                qrow.AddChild(qlabel);

                var trustBtn = new Button { Text = "Trust + activate", FocusMode = Control.FocusModeEnum.All };
                ApplyButtonTheme(trustBtn);
                trustBtn.CustomMinimumSize = new Vector2(180, 32);
                var captured = q;
                trustBtn.Pressed += () =>
                {
                    var moved = promoteQuarantine(captured);
                    if (moved != null) rebuild();
                };
                qrow.AddChild(trustBtn);
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
        closeBtn.Pressed += () => close();
        panel.AddChild(closeBtn);
        closeBtn.CallDeferred("grab_focus");
    }

    // Pushes a mod's enabled state into the matching toggle widget in the open Mods
    // dialog (if any). Use this when something other than a user click flips a mod on
    // or off — e.g. the conflict-resolution prompt — so the dialog stays in sync with
    // the framework's truth. Setter on ToggleSwitch updates visuals only, doesn't
    // re-fire the Toggled event, so this is safe from feedback loops.
    public static void SyncDialogToggle(string modId, bool isEnabled)
    {
        if (_dialogToggles.TryGetValue(modId, out var toggle) && GodotObject.IsInstanceValid(toggle))
            toggle.IsOn = isEnabled;
    }

    // Shows a "two mods declared incompatible — keep which?" dialog. Reuses the same
    // CanvasLayer + harvested-stylebox scaffolding as the Mods dialog, but with two
    // big choice buttons and a "decide later" escape hatch. Layer is 130 (above the
    // Mods dialog at 128) so it can pop on top if the user is still in the Mods menu.
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
