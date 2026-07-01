using Godot;

namespace PratfallModFramework;

// Non-modal activity panel — the replacement for the old fullscreen modal vote dialog.
//
// A small draggable card docks in the top-right corner. A vote shows as an inline entry with
// Yes/No buttons; framework notices (vote results, joins, transfer status) post as passive
// lines beneath it. Gameplay is never blocked: the host Control is click-through, and only the
// card itself captures the mouse. UX inspired by community chat/panel mods (draggable, docked,
// feed-style, queued) but rebuilt from scratch.
//
// The public surface (ShowVote / DismissVote) is unchanged so ModManager's vote queue keeps
// driving it exactly as before — only the presentation swaps modal -> feed.
public class VoteUI : Control
{
    private const float PanelWidth = 360f;
    private const int MaxNotices = 6;

    private PanelContainer _panel = null!;
    private VBoxContainer _feed = null!;

    private Control? _voteEntry;
    private string? _currentVoteModId;
    private System.Action<string, bool>? _onVoteComplete;

    private bool _dragging;
    private Vector2 _dragStartMouse;
    private Vector2 _dragStartPos;
    private bool _positioned;

    public VoteUI()
    {
        Name = "ModFrameworkActivityPanel";
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Ignore; // pass-through: the host never blocks gameplay

        _panel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(PanelWidth, 0),
            MouseFilter = MouseFilterEnum.Stop, // only the card captures the mouse
        };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.06f, 0.08f, 0.93f),
            BorderColor = new Color(1f, 1f, 1f, 0.10f),
            CornerRadiusTopLeft = 10, CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10, CornerRadiusBottomRight = 10,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        _panel.AddThemeStyleboxOverride("panel", sb);
        AddChild(_panel);

        var outer = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        outer.AddThemeConstantOverride("separation", 8);
        _panel.AddChild(outer);

        // Draggable header.
        var header = new HBoxContainer { CustomMinimumSize = new Vector2(0, 22), MouseFilter = MouseFilterEnum.Stop };
        header.GuiInput += OnHeaderInput;
        var title = new Label
        {
            Text = "Mods",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.AddThemeColorOverride("font_color", new Color(0.85f, 0.88f, 0.95f));
        title.AddThemeFontSizeOverride("font_size", 15);
        header.AddChild(title);
        outer.AddChild(header);

        _feed = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _feed.AddThemeConstantOverride("separation", 6);
        outer.AddChild(_feed);

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

    // --- Preserved API (drop-in for the old modal) ---

    public void ShowVote(string modId, string title, string bodyText, int totalPlayers, System.Action<string, bool> onComplete)
    {
        ClearVoteEntry();
        _currentVoteModId = modId;
        _onVoteComplete = onComplete;
        _voteEntry = BuildVoteEntry(title, bodyText);
        _feed.AddChild(_voteEntry);
        _feed.MoveChild(_voteEntry, 0); // keep the actionable vote at the top of the feed
        ShowPanel();
    }

    public void DismissVote()
    {
        ClearVoteEntry();
        _currentVoteModId = null;
        _onVoteComplete = null;
        UpdateVisibility();
    }

    // --- New: passive feed line (vote results / joins / transfer status) ---

    public void AddNotice(string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        lbl.AddThemeColorOverride("font_color", new Color(0.78f, 0.82f, 0.88f));
        lbl.AddThemeFontSizeOverride("font_size", 13);
        _feed.AddChild(lbl);
        TrimNotices();
        ShowPanel();
    }

    // --- internals ---

    private Control BuildVoteEntry(string title, string body)
    {
        var box = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var sb = new StyleBoxFlat
        {
            BgColor = new Color(0.10f, 0.13f, 0.18f, 0.96f),
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 9, ContentMarginRight = 9, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        box.AddThemeStyleboxOverride("panel", sb);

        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddThemeConstantOverride("separation", 6);
        box.AddChild(v);

        var t = new Label { Text = title, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        t.AddThemeColorOverride("font_color", new Color(0.99f, 0.86f, 0.42f));
        t.AddThemeFontSizeOverride("font_size", 14);
        v.AddChild(t);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var b = new Label { Text = body, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
            b.AddThemeColorOverride("font_color", new Color(0.80f, 0.84f, 0.90f));
            b.AddThemeFontSizeOverride("font_size", 12);
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

    // Keep the passive feed short so it never grows unbounded. The active vote entry (a
    // PanelContainer) is excluded — only plain Label notices are trimmed.
    private void TrimNotices()
    {
        var notices = new System.Collections.Generic.List<Node>();
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

    private void UpdateVisibility() => _panel.Visible = _feed.GetChildCount() > 0;

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
}
