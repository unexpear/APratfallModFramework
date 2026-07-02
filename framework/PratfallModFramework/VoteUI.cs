using System.Collections.Generic;
using Godot;

namespace PratfallModFramework;

// The framework's multiplayer hub panel (grew out of the old vote UI). One draggable, docked
// card with a live lobby Players list and a single unified message Log + chat input. The log
// mixes three kinds of entries in chronological order:
//   * regular chat messages   — synced across the lobby (AddChatMessage)
//   * system notices          — local only (AddNotice): vote results, joins, transfer status
//   * vote cards              — local only (ShowVote): an inline system message with Yes/No
//                               buttons; each player sees THEIR own card and answers it.
// Non-modal — the host Control is click-through and only the card captures the mouse, so
// gameplay is never blocked. UX inspired by community chat/panel mods, rebuilt + integrated.
//
// Pure view. Data in via UpdatePlayers / AddChatMessage / AddNotice / ShowVote; outbound chat
// on OnChatSubmit. ModManager owns the wiring (lobby, network, votes).
public class VoteUI : Control
{
    private const float PanelWidth = 520f;
    private const int MaxLogEntries = 80;
    private const float FontScale = 1.5f; // hub text read too small on the large panel

    private CanvasLayer _layer = null!;
    private PanelContainer _panel = null!;
    private Label _playersHeader = null!;
    private VBoxContainer _playersList = null!;
    private ScrollContainer _logScroll = null!;
    private VBoxContainer _log = null!;
    private LineEdit _chatInput = null!;
    private VBoxContainer _toasts = null!; // transient on-screen popups for incoming chat

    private Control? _voteEntry;
    private string? _currentVoteModId;
    private System.Action<string, bool>? _onVoteComplete;

    // Raised when the local player submits a chat line. ModManager broadcasts it + echoes back
    // via AddChatMessage(localName, text) so naming stays consistent with received lines.
    public event System.Action<string>? OnChatSubmit;

    private bool _dragging;
    private Vector2 _dragStartMouse;
    private Vector2 _dragStartPos;
    private bool _userMoved; // once the player drags the panel, stop auto-docking it top-right

    private Label _resizeGrip = null!;
    private bool _userSized; // once the player resizes, use their size instead of the viewport default
    private float _userWidth;
    private float _userLogHeight;
    private bool _resizing;
    private Vector2 _resizeStartMouse;
    private float _resizeStartWidth;
    private float _resizeStartLogH;

    public VoteUI()
    {
        Name = "ModFrameworkHubPanel";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore; // host is click-through; never blocks gameplay

        _panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            MouseFilter = MouseFilterEnum.Stop,
        };
        _panel.AddThemeStyleboxOverride("panel", CardStyle(new Color(0.05f, 0.06f, 0.08f, 0.60f), 10, border: true));
        // _panel is parented later (EnsureLayer) onto a CanvasLayer added DIRECTLY under the
        // window root — matching the framework's proven dialog hosting (MainMenuIntegration
        // dialogs live on CanvasLayer 128/130 under _tree.Root). Parenting it under this Control
        // rendered it too early (during GcManager._Ready, before the menu scene loads), so
        // nothing showed.

        // Transient chat toasts live on the same CanvasLayer but independent of _panel, so new
        // messages flash on-screen even when the hub is closed. Click-through.
        _toasts = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        _toasts.AddThemeConstantOverride("separation", 6);

        var outer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        outer.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(outer);

        // Header (drag handle).
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22), MouseFilter = MouseFilterEnum.Stop };
        header.GuiInput += OnHeaderInput;
        var title = MakeLabel("Mods", 15, new Color(0.85f, 0.88f, 0.95f));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.VerticalAlignment = VerticalAlignment.Center;
        header.AddChild(title);
        var closeBtn = new Button { Text = "✕", Flat = true, CustomMinimumSize = new Vector2(30, 22), FocusMode = FocusModeEnum.None };
        closeBtn.AddThemeColorOverride("font_color", new Color(0.82f, 0.84f, 0.90f));
        closeBtn.Pressed += CloseChat;
        header.AddChild(closeBtn);
        outer.AddChild(header);

        // Players section.
        _playersHeader = MakeLabel("Players", 12, new Color(0.6f, 0.65f, 0.72f));
        outer.AddChild(_playersHeader);
        _playersList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _playersList.AddThemeConstantOverride("separation", 2);
        outer.AddChild(_playersList);

        outer.AddChild(new HSeparator());

        // Unified message log (chat + notices + vote cards).
        _logScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 300),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        outer.AddChild(_logScroll);
        _log = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _log.AddThemeConstantOverride("separation", 3);
        _logScroll.AddChild(_log);

        // Chat input.
        var inputRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inputRow.AddThemeConstantOverride("separation", 6);
        _chatInput = new LineEdit
        {
            PlaceholderText = "Type a message...",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MaxLength = 300,
        };
        _chatInput.TextSubmitted += OnChatEntered;
        inputRow.AddChild(_chatInput);
        var send = new Button { Text = "Send", CustomMinimumSize = new Vector2(56, 0) };
        send.Pressed += () => OnChatEntered(_chatInput.Text);
        inputRow.AddChild(send);
        outer.AddChild(inputRow);

        // Resize grip (bottom-left corner): drag to scale the panel. Bottom-left because the panel
        // is docked to the right edge, so it grows leftward/downward with room to spare.
        var gripRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _resizeGrip = new Label
        {
            Text = "⤡",
            MouseFilter = MouseFilterEnum.Stop,
            MouseDefaultCursorShape = CursorShape.Bdiagsize,
        };
        _resizeGrip.AddThemeColorOverride("font_color", new Color(0.55f, 0.60f, 0.68f));
        _resizeGrip.AddThemeFontSizeOverride("font_size", 22);
        _resizeGrip.GuiInput += OnGripInput;
        gripRow.AddChild(_resizeGrip);
        outer.AddChild(gripRow);

        UpdatePlayers(System.Array.Empty<(string, bool, bool)>());
        _panel.Visible = false;
    }

    public override void _Ready()
    {
        EnsureLayer();
        DockTopRight();
    }

    public override void _ExitTree()
    {
        if (_layer != null && IsInstanceValid(_layer)) _layer.QueueFree();
    }

    // Parent _panel onto a CanvasLayer added DIRECTLY under the window root, matching the
    // framework's proven dialog hosting so it renders above the game's own CanvasLayer UI.
    // Lazy + idempotent: safe to call from _Ready and from every show path regardless of timing.
    private void EnsureLayer()
    {
        if (_layer == null || !IsInstanceValid(_layer))
        {
            _layer = new CanvasLayer { Name = "ModFrameworkHubLayer", Layer = 128 };
            (GetTree()?.Root ?? (Node)GetViewport()).AddChild(_layer);
        }
        if (_panel.GetParent() != _layer)
        {
            _panel.GetParent()?.RemoveChild(_panel);
            _layer.AddChild(_panel);
        }
        if (_toasts.GetParent() != _layer)
        {
            _toasts.GetParent()?.RemoveChild(_toasts);
            _layer.AddChild(_toasts);
        }
    }

    // Opens the hub and focuses chat. Public so the menu's "Click Me" button (and the "\" hotkey
    // poll in ModManager) can open it directly. Keyboard is driven by polling Input.IsKeyPressed
    // rather than _Input, which wasn't firing reliably on this node.
    public void OpenChat()
    {
        EnsureLayer();
        DockTopRight();
        _panel.Visible = true;
        ScrollToBottomDeferred(); // note: no auto-focus — focusing the LineEdit pops the OS touch keyboard
    }

    private void CloseChat()
    {
        _chatInput.ReleaseFocus();
        _panel.Visible = false;
    }

    // Entry points for the ModManager hotkey poll.
    public bool ChatInputHasFocus() => IsInstanceValid(_chatInput) && _chatInput.HasFocus();
    public void Toggle()
    {
        if (IsInstanceValid(_panel) && _panel.Visible) CloseChat();
        else OpenChat();
    }

    // Flash an incoming chat line on-screen for a few seconds (independent of the panel), so
    // messages are visible even when the hub is closed. Fades out and self-removes.
    public void ShowToast(string sender, string text)
    {
        EnsureLayer();
        var vp = GetViewportRect().Size;
        _toasts.Position = new Vector2(24f, vp.Y * 0.55f);

        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore };
        box.AddThemeStyleboxOverride("panel", CardStyle(new Color(0.05f, 0.06f, 0.08f, 0.82f), 8, border: true, margin: 8));
        var lbl = MakeLabel($"{sender}: {text}", 16, new Color(0.90f, 0.93f, 0.98f));
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lbl.CustomMinimumSize = new Vector2(460f, 0);
        box.AddChild(lbl);
        _toasts.AddChild(box);

        while (_toasts.GetChildCount() > 5)
            _toasts.GetChild(0).QueueFree();

        var tween = CreateTween();
        tween.TweenInterval(4.0);
        tween.TweenProperty(box, "modulate:a", 0.0f, 1.0);
        tween.TweenCallback(Callable.From(() => { if (IsInstanceValid(box)) box.QueueFree(); }));
    }

    private void DockTopRight()
    {
        // Deferred so layout settles. Also (re)size the panel relative to the viewport so it's
        // comfortably large on any resolution, then pin it to the top-right by its own width.
        Callable.From(() =>
        {
            if (!IsInstanceValid(_panel)) return;
            var vp = GetViewportRect().Size;
            float w = _userSized ? _userWidth : Mathf.Clamp(vp.X * 0.34f, 520f, 900f);
            float logH = _userSized ? _userLogHeight : Mathf.Clamp(vp.Y * 0.45f, 300f, 680f);
            _panel.CustomMinimumSize = new Vector2(w, 0);
            _logScroll.CustomMinimumSize = new Vector2(0, logH);
            if (_userMoved) return;
            // y=60 clears the menu's "Click Me" button (top-right) so the header + ✕ stay clickable.
            _panel.Position = new Vector2(Mathf.Max(vp.X - w - 22f, 0f), 60f);
        }).CallDeferred();
    }

    // --- Vote (local system message with Yes/No, in the chat flow) ---

    public void ShowVote(string modId, string title, string bodyText, int totalPlayers, System.Action<string, bool> onComplete)
    {
        ClearVoteEntry();
        _currentVoteModId = modId;
        _onVoteComplete = onComplete;
        _voteEntry = BuildVoteEntry(title, bodyText);
        _log.AddChild(_voteEntry);
        ShowPanel();
        ScrollToBottomDeferred();
    }

    public void DismissVote()
    {
        ClearVoteEntry();
        _currentVoteModId = null;
        _onVoteComplete = null;
        UpdateVisibility();
    }

    // --- System notice (local): vote result, join, transfer status ---

    public void AddNotice(string text)
    {
        var lbl = MakeLabel("» " + text, 12, new Color(0.62f, 0.72f, 0.60f));
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AppendLogEntry(lbl);
    }

    // --- Regular chat (synced) ---

    public void AddChatMessage(string sender, string text)
    {
        var lbl = MakeLabel($"{sender}: {text}", 13, new Color(0.85f, 0.88f, 0.93f));
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AppendLogEntry(lbl);
    }

    // --- Live player list ---

    public void UpdatePlayers(IReadOnlyList<(string name, bool isHost, bool isLocal)> players)
    {
        foreach (var c in _playersList.GetChildren()) c.QueueFree();
        _playersHeader.Text = players.Count > 0 ? $"Players ({players.Count})" : "Players";
        foreach (var (name, isHost, isLocal) in players)
        {
            var suffix = isLocal ? "  (you)" : isHost ? "  (host)" : "";
            var color = isHost ? new Color(0.99f, 0.86f, 0.42f)
                : isLocal ? new Color(0.60f, 0.85f, 1.00f)
                : new Color(0.82f, 0.85f, 0.90f);
            var row = MakeLabel("• " + name + suffix, 13, color);
            row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            _playersList.AddChild(row);
        }
        if (players.Count > 0) ShowPanel();
        else UpdateVisibility();
    }

    // --- internals ---

    private void AppendLogEntry(Control entry)
    {
        _log.AddChild(entry);
        // Bound the log, but never trim the active vote card.
        while (_log.GetChildCount() > MaxLogEntries)
        {
            var first = _log.GetChild(0);
            if (first == _voteEntry) break;
            first.QueueFree();
        }
        ShowPanel();
        ScrollToBottomDeferred();
    }

    private Control BuildVoteEntry(string title, string body)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeStyleboxOverride("panel", CardStyle(new Color(0.12f, 0.11f, 0.05f, 0.96f), 6, border: true, margin: 8, borderColor: new Color(0.99f, 0.86f, 0.42f, 0.5f)));

        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 6);
        box.AddChild(v);

        var t = MakeLabel(title, 14, new Color(0.99f, 0.86f, 0.42f));
        t.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        t.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(t);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var b = MakeLabel(body, 12, new Color(0.80f, 0.84f, 0.90f));
            b.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            b.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            v.AddChild(b);
        }

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 8);
        var yes = new Button { Text = "Yes", CustomMinimumSize = new Vector2(74, 30), FocusMode = FocusModeEnum.All };
        yes.Pressed += () => SubmitVote(true);
        var no = new Button { Text = "No", CustomMinimumSize = new Vector2(74, 30), FocusMode = FocusModeEnum.All };
        no.Pressed += () => SubmitVote(false);
        row.AddChild(yes);
        row.AddChild(no);
        v.AddChild(row);
        yes.CallDeferred("grab_focus");
        return box;
    }

    private void SubmitVote(bool yes)
    {
        var id = _currentVoteModId;
        var cb = _onVoteComplete;
        ClearVoteEntry();
        _currentVoteModId = null;
        _onVoteComplete = null;
        if (id != null)
        {
            AddNotice(yes ? "You voted Yes" : "You voted No");
            cb?.Invoke(id, yes);
        }
        UpdateVisibility();
    }

    private void ClearVoteEntry()
    {
        if (_voteEntry != null && IsInstanceValid(_voteEntry))
            _voteEntry.QueueFree();
        _voteEntry = null;
    }

    private void OnChatEntered(string text)
    {
        text = (text ?? "").Trim();
        _chatInput.Clear();
        _chatInput.ReleaseFocus(); // hand keyboard back to the game after sending
        if (text.Length == 0) return;
        OnChatSubmit?.Invoke(text);
    }

    private void ScrollToBottomDeferred() => Callable.From(ScrollToBottom).CallDeferred();

    private void ScrollToBottom()
    {
        if (IsInstanceValid(_logScroll))
            _logScroll.ScrollVertical = (int)_logScroll.GetVScrollBar().MaxValue;
    }

    private void ShowPanel()
    {
        EnsureLayer();
        DockTopRight();
        _panel.Visible = true;
    }

    private void UpdateVisibility() =>
        _panel.Visible = _log.GetChildCount() > 0 || _playersList.GetChildCount() > 0;

    private void OnHeaderInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed) { _dragging = true; _dragStartMouse = GetGlobalMousePosition(); _dragStartPos = _panel.Position; }
            else _dragging = false;
        }
        else if (e is InputEventMouseMotion && _dragging)
        {
            _userMoved = true;
            var vp = GetViewportRect().Size;
            var np = _dragStartPos + (GetGlobalMousePosition() - _dragStartMouse);
            np.X = Mathf.Clamp(np.X, 0f, Mathf.Max(vp.X - _panel.Size.X, 0f));
            np.Y = Mathf.Clamp(np.Y, 0f, Mathf.Max(vp.Y - 60f, 0f));
            _panel.Position = np;
        }
    }

    private void OnGripInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _resizing = true;
                _resizeStartMouse = GetGlobalMousePosition();
                _resizeStartWidth = _panel.Size.X;
                _resizeStartLogH = _logScroll.Size.Y;
                _userSized = true;
            }
            else _resizing = false;
        }
        else if (e is InputEventMouseMotion && _resizing)
        {
            var vp = GetViewportRect().Size;
            var d = GetGlobalMousePosition() - _resizeStartMouse;
            // Grip is bottom-left: drag left => wider, drag down => taller.
            _userWidth = Mathf.Clamp(_resizeStartWidth - d.X, 320f, vp.X - 40f);
            _userLogHeight = Mathf.Clamp(_resizeStartLogH + d.Y, 120f, vp.Y - 180f);
            ApplySize(vp);
        }
    }

    private void ApplySize(Vector2 vp)
    {
        _panel.CustomMinimumSize = new Vector2(_userWidth, 0);
        _logScroll.CustomMinimumSize = new Vector2(0, _userLogHeight);
        if (!_userMoved)
            _panel.Position = new Vector2(Mathf.Max(vp.X - _userWidth - 22f, 0f), 60f);
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", Mathf.RoundToInt(fontSize * FontScale));
        return l;
    }

    private static StyleBoxFlat CardStyle(Color bg, int radius, bool border, int margin = 10, Color? borderColor = null)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            CornerRadiusTopLeft = radius, CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius, CornerRadiusBottomRight = radius,
            ContentMarginLeft = margin, ContentMarginRight = margin,
            ContentMarginTop = margin, ContentMarginBottom = margin,
        };
        if (border)
        {
            sb.BorderColor = borderColor ?? new Color(1f, 1f, 1f, 0.10f);
            sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = sb.BorderWidthBottom = 1;
        }
        return sb;
    }
}
