using System.Collections.Generic;
using Godot;

namespace PratfallModFramework;

// The framework's multiplayer hub panel (grew out of the old vote UI). One draggable, docked
// card with three sections: a live lobby Players list, a vote + notice Feed, and a synced text
// Chat. Non-modal — the host Control is click-through and only the card captures the mouse, so
// nothing blocks gameplay. UX inspired by community chat/panel mods, rebuilt + integrated so a
// single panel replaces the awkward vote modal *and* the separate chat/player-panel mods.
//
// Pure view. Data comes in via UpdatePlayers / AddChatMessage / ShowVote / AddNotice; outbound
// chat is raised on OnChatSubmit. ModManager owns all the wiring (lobby, network, votes).
public class VoteUI : Control
{
    private const float PanelWidth = 380f;
    private const int MaxNotices = 5;
    private const int MaxChatLines = 60;

    private PanelContainer _panel = null!;
    private Label _playersHeader = null!;
    private VBoxContainer _playersList = null!;
    private VBoxContainer _feed = null!;
    private ScrollContainer _chatScroll = null!;
    private VBoxContainer _chatLog = null!;
    private LineEdit _chatInput = null!;

    private Control? _voteEntry;
    private string? _currentVoteModId;
    private System.Action<string, bool>? _onVoteComplete;

    // Raised when the local player submits a chat line. ModManager broadcasts it + echoes it
    // back via AddChatMessage(localName, text) so naming stays consistent with received lines.
    public event System.Action<string>? OnChatSubmit;

    private bool _dragging;
    private Vector2 _dragStartMouse;
    private Vector2 _dragStartPos;
    private bool _positioned;

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
        _panel.AddThemeStyleboxOverride("panel", CardStyle(new Color(0.05f, 0.06f, 0.08f, 0.94f), 10, border: true));
        AddChild(_panel);

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
        outer.AddChild(header);

        // Players section.
        _playersHeader = MakeLabel("Players", 12, new Color(0.6f, 0.65f, 0.72f));
        outer.AddChild(_playersHeader);
        _playersList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _playersList.AddThemeConstantOverride("separation", 2);
        outer.AddChild(_playersList);

        outer.AddChild(new HSeparator());

        // Feed section (votes + notices + transfer progress).
        _feed = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _feed.AddThemeConstantOverride("separation", 6);
        outer.AddChild(_feed);

        outer.AddChild(new HSeparator());

        // Chat section.
        _chatScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 120),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        outer.AddChild(_chatScroll);
        _chatLog = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _chatLog.AddThemeConstantOverride("separation", 2);
        _chatScroll.AddChild(_chatLog);

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

        UpdatePlayers(System.Array.Empty<(string, bool, bool)>());
        _panel.Visible = false;
    }

    public override void _Ready() => DockTopRight();

    private void DockTopRight()
    {
        if (_positioned) return;
        var vp = GetViewportRect().Size;
        _panel.Position = new Vector2(Mathf.Max(vp.X - PanelWidth - 18f, 0f), 18f);
        _positioned = true;
    }

    // --- Preserved vote API (drop-in for the old modal) ---

    public void ShowVote(string modId, string title, string bodyText, int totalPlayers, System.Action<string, bool> onComplete)
    {
        ClearVoteEntry();
        _currentVoteModId = modId;
        _onVoteComplete = onComplete;
        _voteEntry = BuildVoteEntry(title, bodyText);
        _feed.AddChild(_voteEntry);
        _feed.MoveChild(_voteEntry, 0); // actionable vote sits at the top of the feed
        ShowPanel();
    }

    public void DismissVote()
    {
        ClearVoteEntry();
        _currentVoteModId = null;
        _onVoteComplete = null;
        UpdateVisibility();
    }

    // --- Feed notices (vote results, joins, transfer status) ---

    public void AddNotice(string text)
    {
        var lbl = MakeLabel(text, 13, new Color(0.78f, 0.82f, 0.88f));
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _feed.AddChild(lbl);
        TrimNotices();
        ShowPanel();
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
        // In a lobby (members present) the hub is a useful persistent surface — show it.
        // When the roster empties, re-evaluate (hide if nothing else is showing).
        if (players.Count > 0) ShowPanel();
        else UpdateVisibility();
    }

    // --- Chat ---

    public void AddChatMessage(string sender, string text)
    {
        var lbl = MakeLabel($"{sender}: {text}", 13, new Color(0.85f, 0.88f, 0.93f));
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _chatLog.AddChild(lbl);
        while (_chatLog.GetChildCount() > MaxChatLines) _chatLog.GetChild(0).QueueFree();
        ShowPanel();
        Callable.From(ScrollChatToBottom).CallDeferred();
    }

    private void OnChatEntered(string text)
    {
        text = (text ?? "").Trim();
        _chatInput.Clear();
        if (text.Length == 0) return;
        OnChatSubmit?.Invoke(text);
    }

    private void ScrollChatToBottom()
    {
        if (IsInstanceValid(_chatScroll))
            _chatScroll.ScrollVertical = (int)_chatScroll.GetVScrollBar().MaxValue;
    }

    // --- internals ---

    private Control BuildVoteEntry(string title, string body)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeStyleboxOverride("panel", CardStyle(new Color(0.10f, 0.13f, 0.18f, 0.96f), 6, border: false, margin: 8));

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
        UpdateVisibility();
        if (id != null) cb?.Invoke(id, yes);
    }

    private void ClearVoteEntry()
    {
        if (_voteEntry != null && IsInstanceValid(_voteEntry))
            _voteEntry.QueueFree();
        _voteEntry = null;
    }

    private void TrimNotices()
    {
        var notices = new List<Node>();
        foreach (var c in _feed.GetChildren())
            if (c != _voteEntry && c is Label) notices.Add(c);
        for (var i = 0; i < notices.Count - MaxNotices; i++)
            notices[i].QueueFree();
    }

    private void ShowPanel()
    {
        DockTopRight();
        _panel.Visible = true;
    }

    // The hub is a persistent surface once it has any content (players/feed/chat), so it stays
    // visible; only fully-empty resets hide it.
    private void UpdateVisibility()
    {
        _panel.Visible = _feed.GetChildCount() > 0 || _chatLog.GetChildCount() > 0 || _playersList.GetChildCount() > 0;
    }

    private void OnHeaderInput(InputEvent e)
    {
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed) { _dragging = true; _dragStartMouse = GetGlobalMousePosition(); _dragStartPos = _panel.Position; }
            else _dragging = false;
        }
        else if (e is InputEventMouseMotion && _dragging)
        {
            var vp = GetViewportRect().Size;
            var np = _dragStartPos + (GetGlobalMousePosition() - _dragStartMouse);
            np.X = Mathf.Clamp(np.X, 0f, Mathf.Max(vp.X - PanelWidth, 0f));
            np.Y = Mathf.Clamp(np.Y, 0f, Mathf.Max(vp.Y - 60f, 0f));
            _panel.Position = np;
        }
    }

    private static Label MakeLabel(string text, int fontSize, Color color)
    {
        var l = new Label { Text = text, MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeColorOverride("font_color", color);
        l.AddThemeFontSizeOverride("font_size", fontSize);
        return l;
    }

    private static StyleBoxFlat CardStyle(Color bg, int radius, bool border, int margin = 10)
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
            sb.BorderColor = new Color(1f, 1f, 1f, 0.10f);
            sb.BorderWidthLeft = sb.BorderWidthRight = sb.BorderWidthTop = sb.BorderWidthBottom = 1;
        }
        return sb;
    }
}
