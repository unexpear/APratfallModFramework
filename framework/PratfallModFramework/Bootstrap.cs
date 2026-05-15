using Godot;

namespace PratfallModFramework;

public static class Bootstrap
{
    private static ModManager? _instance;
    private static int _initialized;

    public static void Init()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;
        GD.Print("[ModFramework] Bootstrap.Init() called");
        Callable.From(InitOnMainThread).CallDeferred();
    }

    private static void InitOnMainThread()
    {
        if (_instance != null) return;

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            ModRuntime.MarkGodotRuntimeReady();

            try
            {
                _instance = new ModManager();
                _instance.Initialize(tree);
                GD.Print("[ModFramework] Framework initialized");
                ShowStartupStatus(tree, "Mod Framework", "Pratfall Mod Framework loaded successfully!", false);
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[ModFramework] Init failed: {ex.GetType().Name}: {ex.Message}");
                ShowStartupStatus(
                    tree,
                    "Mod Framework",
                    $"Pratfall Mod Framework has failed\n\n{ex.GetType().Name}: {ex.Message}",
                    true);
            }
        }
    }

    public static void Shutdown()
    {
        _instance?.Shutdown();
        ModRuntime.MarkGodotRuntimeStopped();
        _instance = null;
        _initialized = 0;
        GD.Print("[ModFramework] Shutdown complete");
    }

    private static void ShowStartupStatus(SceneTree tree, string titleText, string bodyText, bool isError)
    {
        var existing = tree.Root.GetNodeOrNull("ModFrameworkStartupStatus");
        existing?.QueueFree();

        var overlay = new Control
        {
            Name = "ModFrameworkStartupStatus",
            MouseFilter = Control.MouseFilterEnum.Pass,
        };
        SetFullRect(overlay);
        tree.Root.AddChild(overlay);

        var scrim = new ColorRect
        {
            Color = new Color(0.02f, 0.05f, 0.07f, 0.5f),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        SetFullRect(scrim);
        overlay.AddChild(scrim);

        var center = new CenterContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        SetFullRect(center);
        overlay.AddChild(center);

        var shell = new PanelContainer
        {
            CustomMinimumSize = new Vector2(480, isError ? 250 : 220),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        shell.AddThemeStyleboxOverride("panel", CreateStatusPanelStyle(isError));
        center.AddChild(shell);

        var margin = new MarginContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_top", 22);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        shell.AddChild(margin);

        var layout = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        layout.AddThemeConstantOverride("separation", 16);
        margin.AddChild(layout);

        var title = new Label
        {
            Text = titleText,
            HorizontalAlignment = HorizontalAlignment.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        title.AddThemeFontSizeOverride("font_size", 28);
        title.AddThemeColorOverride("font_color", new Color(0.97f, 0.94f, 0.78f));
        layout.AddChild(title);

        var body = new Label
        {
            Text = bodyText,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(432, isError ? 96 : 72),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        body.AddThemeFontSizeOverride("font_size", 18);
        body.AddThemeColorOverride("font_color", new Color(0.92f, 0.96f, 0.98f));
        layout.AddChild(body);

        var okButton = new Button
        {
            Text = "OK",
            FocusMode = Control.FocusModeEnum.All,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            CustomMinimumSize = new Vector2(180, 52),
        };
        okButton.Pressed += () => overlay.QueueFree();
        layout.AddChild(okButton);

        if (!isError)
        {
            var autoCloseTimer = new Godot.Timer
            {
                OneShot = true,
                WaitTime = 2.0,
                ProcessMode = Node.ProcessModeEnum.Always,
            };
            autoCloseTimer.Timeout += () =>
            {
                autoCloseTimer.QueueFree();
                if (GodotObject.IsInstanceValid(overlay))
                    overlay.QueueFree();
            };
            overlay.AddChild(autoCloseTimer);
            autoCloseTimer.Start();
        }

        overlay.GuiInput += (InputEvent @event) =>
        {
            if (!IsActionPressed(@event, "ui_cancel"))
                return;

            overlay.QueueFree();
            overlay.AcceptEvent();
        };
        okButton.CallDeferred("grab_focus");
    }

    private static StyleBoxFlat CreateStatusPanelStyle(bool isError)
    {
        var style = new StyleBoxFlat
        {
            BgColor = isError ? new Color(0.31f, 0.13f, 0.15f, 0.98f) : new Color(0.17f, 0.36f, 0.43f, 0.98f),
            BorderColor = isError ? new Color(0.63f, 0.23f, 0.25f) : new Color(0.05f, 0.13f, 0.16f),
            ShadowColor = new Color(0f, 0f, 0f, 0.28f),
            ShadowSize = 10,
        };
        style.SetBorderWidthAll(6);
        style.SetCornerRadiusAll(22);
        return style;
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

    private static bool IsActionPressed(InputEvent @event, string actionName)
    {
        return @event is InputEventAction actionEvent
            && actionEvent.Pressed
            && actionEvent.Action == actionName;
    }
}
