using Godot;

namespace PratfallModFramework;

// Mods dialog — the main "Installed Mods" list opened by the framework's
// injected Mods button. One card per local mod with title/author/description,
// the ℹ Info + 🔍 Scan buttons, the on/off ToggleSwitch, and a ⚠ compatibility
// badge when the cached report flags the mod. Lives at CanvasLayer 128 so it
// renders above the main menu but below the inspection / scan / acquisition /
// conflict prompts (layer 130).
public static partial class MainMenuIntegration
{
    // Called externally (e.g. by ModManager when WorkshopSubscriber reports a
    // live Workshop install) to repaint the Mods dialog if the user has it
    // open. Implementation: close + reopen. The reopen re-reads the mod list
    // so newly-discovered mods appear immediately. No-op when dialog is closed
    // or scene tree isn't available.
    public static void RefreshModsDialogIfOpen()
    {
        if (_tree == null) return;
        var existing = _tree.Root.GetNodeOrNull("ModFrameworkDialogLayer");
        if (existing == null) return; // dialog not open — nothing to refresh
        existing.QueueFree();
        // Defer the reopen by one frame so QueueFree completes before we
        // re-add a layer with the same name.
        Callable.From(() => OnModsButtonPressed()).CallDeferred();
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

                // ℹ️ — read-only metadata (manifest fields, file listing, declared patches).
                // No side effects — just shows what the mod claims to be.
                if (_inspectMod != null)
                {
                    var infoBtn = new Button
                    {
                        Text = "ℹ",
                        FocusMode = Control.FocusModeEnum.All,
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                        TooltipText = "Mod info (manifest, files, hashes, declared patches)",
                    };
                    ApplyButtonTheme(infoBtn);
                    infoBtn.CustomMinimumSize = new Vector2(46f, Math.Max(GetReferenceButtonHeight() * 0.7f, 36f));
                    var capturedIdInfo = mod.Id;
                    infoBtn.Pressed += () =>
                    {
                        var report = _inspectMod(capturedIdInfo);
                        if (report != null && _tree != null)
                            ShowInspectionPanel(_tree, report);
                    };
                    topRow.AddChild(infoBtn);
                }

                // 🔍 — IL safety scanner. Walks the mod's DLL and reports concerning
                // API usage. Running this counts as user verification → marks the mod's
                // fingerprint as checked (releases the load gate).
                if (_scanMod != null)
                {
                    var scanBtn = new Button
                    {
                        Text = "🔍",
                        FocusMode = Control.FocusModeEnum.All,
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                        TooltipText = "Scan this mod for dangerous API usage (Process, network, registry, P/Invoke, …)",
                    };
                    ApplyButtonTheme(scanBtn);
                    scanBtn.CustomMinimumSize = new Vector2(46f, Math.Max(GetReferenceButtonHeight() * 0.7f, 36f));
                    var capturedIdScan = mod.Id;
                    scanBtn.Pressed += () =>
                    {
                        var report = _scanMod(capturedIdScan);
                        if (report != null && _tree != null)
                            ShowScanPanel(_tree, report);
                    };
                    topRow.AddChild(scanBtn);
                }

                // ⚙ — per-mod settings panel. Only shown if the mod has actually
                // called ModConfig.For(modId).Bind<T>(...) at least once. Mods
                // that don't use ModConfig don't get a button (zero clutter).
                if (ModConfig.GetAllEntries(mod.Id).Count > 0)
                {
                    var settingsBtn = new Button
                    {
                        Text = "⚙",
                        FocusMode = Control.FocusModeEnum.All,
                        SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd,
                        TooltipText = "Mod settings (auto-generated from ModConfig)",
                    };
                    ApplyButtonTheme(settingsBtn);
                    settingsBtn.CustomMinimumSize = new Vector2(46f, Math.Max(GetReferenceButtonHeight() * 0.7f, 36f));
                    var capturedIdSettings = mod.Id;
                    var capturedNameSettings = string.IsNullOrWhiteSpace(mod.Name) ? mod.Id : mod.Name;
                    settingsBtn.Pressed += () =>
                    {
                        if (_tree != null)
                            ShowSettingsPanel(_tree, capturedIdSettings, capturedNameSettings);
                    };
                    topRow.AddChild(settingsBtn);
                }

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
}
