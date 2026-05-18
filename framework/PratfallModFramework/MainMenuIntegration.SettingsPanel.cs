using Godot;

namespace PratfallModFramework;

// In-game settings editor for mods that use ModConfig.For(modId).Bind<T>(...).
// Modeled on the Inspection / Scan panels — separate modal at CanvasLayer 130,
// opens from a ⚙ button on the mod's card row in the main Mods dialog.
//
// Entries are grouped by Section. Per-entry, the widget is chosen by type +
// constraint:
//
//   bool                                    → ToggleSwitch (our existing widget)
//   int/long  with AcceptableValueRange     → HSlider + min/max/current labels
//   float/double with AcceptableValueRange  → HSlider with step, current label
//   int/long/float/double, no constraint    → SpinBox
//   string with AcceptableValueList         → OptionButton (dropdown)
//   string, no constraint                   → LineEdit
//   enum                                    → OptionButton (auto-populated from
//                                              Enum.GetNames)
//
// Per-row "↺" reset button calls ConfigEntry.ResetToDefault() then refreshes
// the widget's visual value.
//
// Two-way bind philosophy: widget interaction → ConfigEntry.BoxedValue setter
// (which persists + fires OnChange). External value mutations (mod code changes
// a Value while the panel is open) are NOT reflected live — reopen the panel
// to see them. Acceptable v1 trade-off; programmatic mid-panel mutations are
// rare and the panel rebuilds on each open.
public static partial class MainMenuIntegration
{
    public static void ShowSettingsPanel(SceneTree tree, string modId, string displayName)
    {
        if (tree?.Root == null || string.IsNullOrWhiteSpace(modId)) return;
        var entries = ModConfig.GetAllEntries(modId);
        if (entries.Count == 0) return; // nothing to show — caller should have hidden the button

        var existing = tree.Root.GetNodeOrNull("ModFrameworkSettingsLayer");
        if (existing != null) existing.QueueFree();

        var canvasLayer = new CanvasLayer { Name = "ModFrameworkSettingsLayer", Layer = 130 };
        tree.Root.AddChild(canvasLayer);

        var overlay = new Control { Name = "ModFrameworkSettingsDialog", MouseFilter = Control.MouseFilterEnum.Stop };
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
            Text = $"Settings: {(string.IsNullOrWhiteSpace(displayName) ? modId : displayName)}",
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
        body.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(body);

        // Group entries by Section. Stable sort within section by Key for predictable layout.
        var bySection = entries
            .GroupBy(e => e.Section ?? "")
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var section in bySection)
        {
            var sectionName = string.IsNullOrEmpty(section.Key) ? "General" : section.Key;
            AddInspectHeader(body, sectionName);
            foreach (var entry in section.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
            {
                AddSettingsRow(body, entry, canvasLayer, modId);
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

    private static void AddSettingsRow(VBoxContainer parent, IConfigEntry entry, CanvasLayer rootLayer, string modId)
    {
        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);

        var label = new Label
        {
            Text = entry.Key,
            CustomMinimumSize = new Vector2(180, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ApplyFont(label, Math.Max(_buttonFontSize - 1, 14));
        label.AddThemeColorOverride("font_color", new Color(0.85f, 0.92f, 0.95f));
        if (entry.Description?.Tooltip is { } tip && !string.IsNullOrWhiteSpace(tip))
        {
            label.TooltipText = tip;
            label.MouseFilter = Control.MouseFilterEnum.Stop;
        }
        row.AddChild(label);

        // Widget column expands to fill the available space between label and reset button.
        Control widget = CreateWidgetForEntry(entry, out Action refreshWidget);
        widget.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(widget);

        // Per-entry reset to default. After reset, refresh the widget visually
        // (the value already changed via ConfigEntry.ResetToDefault → setter).
        var resetBtn = new Button
        {
            Text = "↺",
            TooltipText = "Reset to default",
            FocusMode = Control.FocusModeEnum.All,
            CustomMinimumSize = new Vector2(40, Math.Max(GetReferenceButtonHeight() * 0.7f, 32f)),
        };
        ApplyButtonTheme(resetBtn);
        resetBtn.Pressed += () =>
        {
            try
            {
                entry.ResetToDefault();
                refreshWidget?.Invoke();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[ModFramework] SettingsPanel reset failed for {modId}/[{entry.Section}].{entry.Key}: {ex.GetType().Name}: {ex.Message}");
            }
        };
        row.AddChild(resetBtn);
    }

    // Creates the right widget for the entry's type + constraint. Returns the
    // widget control + an Action that refreshes its visual value from the
    // entry's current BoxedValue (used after Reset).
    private static Control CreateWidgetForEntry(IConfigEntry entry, out Action refresh)
    {
        var t = entry.ValueType;
        var constraint = entry.Description?.Constraint;

        // bool → ToggleSwitch (our existing custom widget)
        if (t == typeof(bool))
        {
            var current = (bool)(entry.BoxedValue ?? false);
            var toggle = new ToggleSwitch(current);
            toggle.Toggled += v => SafeSet(entry, v);
            refresh = () => toggle.IsOn = (bool)(entry.BoxedValue ?? false);
            return toggle;
        }

        // string with AcceptableValueList → OptionButton (dropdown)
        if (t == typeof(string) && constraint?.AllowedValues is { Count: > 0 } strChoices)
        {
            var opt = new OptionButton();
            ApplyButtonTheme(opt);
            foreach (var v in strChoices) opt.AddItem(v?.ToString() ?? "");
            SelectByValue(opt, strChoices, entry.BoxedValue);
            opt.ItemSelected += (long idx) =>
            {
                if (idx >= 0 && idx < strChoices.Count)
                    SafeSet(entry, strChoices[(int)idx]);
            };
            refresh = () => SelectByValue(opt, strChoices, entry.BoxedValue);
            return opt;
        }

        // string (no constraint) → LineEdit
        if (t == typeof(string))
        {
            var line = new LineEdit
            {
                Text = entry.BoxedValue?.ToString() ?? "",
                CustomMinimumSize = new Vector2(200, 0),
            };
            line.TextChanged += text => SafeSet(entry, text);
            refresh = () => line.Text = entry.BoxedValue?.ToString() ?? "";
            return line;
        }

        // enum → OptionButton populated from Enum.GetNames
        if (t.IsEnum)
        {
            var names = Enum.GetNames(t);
            var values = Enum.GetValues(t);
            var opt = new OptionButton();
            ApplyButtonTheme(opt);
            foreach (var n in names) opt.AddItem(n);
            var currentName = entry.BoxedValue?.ToString();
            for (int i = 0; i < names.Length; i++)
                if (string.Equals(names[i], currentName, StringComparison.Ordinal)) { opt.Selected = i; break; }
            opt.ItemSelected += (long idx) =>
            {
                if (idx >= 0 && idx < values.Length)
                    SafeSet(entry, values.GetValue((int)idx));
            };
            refresh = () =>
            {
                var cur = entry.BoxedValue?.ToString();
                for (int i = 0; i < names.Length; i++)
                    if (string.Equals(names[i], cur, StringComparison.Ordinal)) { opt.Selected = i; break; }
            };
            return opt;
        }

        // numeric with AcceptableValueRange → HSlider + value label
        if (IsNumericType(t) && constraint is { Lower: not null, Upper: not null })
        {
            var min = Convert.ToDouble(constraint.Lower);
            var max = Convert.ToDouble(constraint.Upper);
            var step = (t == typeof(int) || t == typeof(long)) ? 1.0 : Math.Max((max - min) / 100.0, 0.01);
            var current = Convert.ToDouble(entry.BoxedValue ?? 0);

            var slider = new HSlider
            {
                MinValue = min,
                MaxValue = max,
                Step = step,
                Value = current,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(200, 0),
            };
            var valueLabel = new Label
            {
                Text = FormatNumericValue(current, t),
                CustomMinimumSize = new Vector2(60, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ApplyFont(valueLabel, Math.Max(_buttonFontSize - 1, 14));
            valueLabel.AddThemeColorOverride("font_color", new Color(0.75f, 0.9f, 0.93f));
            slider.ValueChanged += d =>
            {
                valueLabel.Text = FormatNumericValue(d, t);
                SafeSet(entry, ConvertNumeric(d, t));
            };

            var wrapper = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            wrapper.AddThemeConstantOverride("separation", 8);
            wrapper.AddChild(slider);
            wrapper.AddChild(valueLabel);

            refresh = () =>
            {
                var v = Convert.ToDouble(entry.BoxedValue ?? 0);
                slider.Value = v;
                valueLabel.Text = FormatNumericValue(v, t);
            };
            return wrapper;
        }

        // numeric (no constraint) → SpinBox
        if (IsNumericType(t))
        {
            var spin = new SpinBox
            {
                MinValue = double.MinValue / 2,
                MaxValue = double.MaxValue / 2,
                Step = (t == typeof(int) || t == typeof(long)) ? 1 : 0.1,
                Value = Convert.ToDouble(entry.BoxedValue ?? 0),
                CustomMinimumSize = new Vector2(150, 0),
            };
            spin.ValueChanged += d => SafeSet(entry, ConvertNumeric(d, t));
            refresh = () => spin.Value = Convert.ToDouble(entry.BoxedValue ?? 0);
            return spin;
        }

        // Fallback for unknown types — read-only label, no edit.
        var fallback = new Label { Text = $"(unsupported type: {t.Name})" };
        ApplyFont(fallback, Math.Max(_buttonFontSize - 2, 12));
        fallback.AddThemeColorOverride("font_color", new Color(0.7f, 0.7f, 0.55f));
        refresh = () => fallback.Text = $"(unsupported type: {t.Name})";
        return fallback;
    }

    private static void SafeSet(IConfigEntry entry, object? newValue)
    {
        try
        {
            entry.BoxedValue = newValue;
        }
        catch (Exception ex)
        {
            // Constraint rejection or type conversion failure. Log + ignore — the
            // widget will hold the bad value visually but the underlying entry
            // stayed at its previous value (constraint enforcement happens before
            // the setter lands). Per-row "↺" Reset can recover.
            GD.PrintErr($"[ModFramework] SettingsPanel: rejected value '{newValue}' for [{entry.Section}].{entry.Key}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SelectByValue(OptionButton opt, IReadOnlyList<object> values, object? current)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (Equals(values[i], current)) { opt.Selected = i; return; }
        }
        if (values.Count > 0) opt.Selected = 0;
    }

    private static bool IsNumericType(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double);

    private static object ConvertNumeric(double d, Type targetType)
    {
        if (targetType == typeof(int)) return (int)Math.Round(d);
        if (targetType == typeof(long)) return (long)Math.Round(d);
        if (targetType == typeof(float)) return (float)d;
        if (targetType == typeof(double)) return d;
        return d;
    }

    private static string FormatNumericValue(double d, Type type)
    {
        if (type == typeof(int) || type == typeof(long)) return ((long)Math.Round(d)).ToString();
        // 2 decimal places for floats; trim trailing zeros for cleanliness.
        return d.ToString("0.##");
    }
}
