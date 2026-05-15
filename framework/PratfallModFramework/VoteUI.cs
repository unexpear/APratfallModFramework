using Godot;

namespace PratfallModFramework;

public class VoteUI : Control
{
    private VBoxContainer _container = null!;
    private Label _titleLabel = null!;
    private Label _modInfoLabel = null!;
    private HBoxContainer _buttonRow = null!;
    private Button _yesBtn = null!;
    private Button _noBtn = null!;
    private Godot.Timer _timeout = null!;
    private Godot.Timer _nativeDialogRetryTimer = null!;
    private string? _currentVoteModId;
    private System.Action<string, bool>? _onVoteComplete;
    private bool _usingNativeDialog;
    private string _pendingTitle = "";
    private string _pendingBody = "";
    private int _nativeDialogRetriesRemaining;

    public VoteUI()
    {
        AnchorRight = 1;
        AnchorBottom = 1;
        MouseFilter = MouseFilterEnum.Pass;
        GuiInput += OnGuiInput;

        _container = new VBoxContainer
        {
            AnchorLeft = 0.3f,
            AnchorRight = 0.7f,
            AnchorTop = 0.35f,
            AnchorBottom = 0.65f,
            OffsetLeft = 0,
            OffsetRight = 0,
            OffsetTop = 0,
            OffsetBottom = 0,
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_container);

        var bg = new ColorRect
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            Color = new Color(0, 0, 0, 0.85f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _container.AddChild(bg);

        _titleLabel = new Label
        {
            Text = "MOD VOTE",
            HorizontalAlignment = HorizontalAlignment.Center,
            ThemeTypeVariation = "Title",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _container.AddChild(_titleLabel);

        _modInfoLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _container.AddChild(_modInfoLabel);

        _buttonRow = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _container.AddChild(_buttonRow);

        _yesBtn = new Button
        {
            Text = "  YES  ",
            FocusMode = FocusModeEnum.All
        };
        _yesBtn.Pressed += () => SubmitVote(true);
        _buttonRow.AddChild(_yesBtn);

        _noBtn = new Button
        {
            Text = "  NO  ",
            FocusMode = FocusModeEnum.All
        };
        _noBtn.Pressed += () => SubmitVote(false);
        _buttonRow.AddChild(_noBtn);
        _yesBtn.FocusNext = _yesBtn.GetPathTo(_noBtn);
        _yesBtn.SetFocusNeighbor(Side.Right, _yesBtn.GetPathTo(_noBtn));
        _noBtn.FocusPrevious = _noBtn.GetPathTo(_yesBtn);
        _noBtn.SetFocusNeighbor(Side.Left, _noBtn.GetPathTo(_yesBtn));

        _timeout = new Godot.Timer
        {
            OneShot = true,
            WaitTime = 15.0
        };
        _timeout.Timeout += OnTimeout;
        AddChild(_timeout);

        _nativeDialogRetryTimer = new Godot.Timer
        {
            OneShot = true,
            WaitTime = 0.25,
            ProcessMode = Node.ProcessModeEnum.Always
        };
        _nativeDialogRetryTimer.Timeout += OnNativeDialogRetryTimeout;
        AddChild(_nativeDialogRetryTimer);

        Hide();
    }

    public void ShowVote(string modId, string title, string bodyText, int totalPlayers,
        System.Action<string, bool> onComplete)
    {
        _currentVoteModId = modId;
        _onVoteComplete = onComplete;
        _usingNativeDialog = false;
        _pendingTitle = title;
        _pendingBody = bodyText;
        _timeout.Start();

        if (TryShowNativeDialog())
            return;

        _nativeDialogRetriesRemaining = 40;
        _nativeDialogRetryTimer.Start();
    }

    public void DismissVote()
    {
        if (_currentVoteModId == null && !Visible)
            return;

        if (_usingNativeDialog)
        {
            _usingNativeDialog = false;
            NativeDialogBridge.DismissActive();
        }

        _nativeDialogRetryTimer.Stop();

        Reset();
    }

    private void SubmitVote(bool voteYes)
    {
        if (_currentVoteModId == null) return;
        var id = _currentVoteModId;
        _onVoteComplete?.Invoke(id, voteYes);
        Reset();
    }

    private void OnTimeout()
    {
        if (_currentVoteModId == null) return;

        if (_usingNativeDialog)
        {
            _usingNativeDialog = false;
            NativeDialogBridge.DismissActive();
        }

        _nativeDialogRetryTimer.Stop();

        var id = _currentVoteModId;
        _onVoteComplete?.Invoke(id, false);
        Reset();
    }

    private void Reset()
    {
        _currentVoteModId = null;
        _onVoteComplete = null;
        _usingNativeDialog = false;
        _pendingTitle = "";
        _pendingBody = "";
        _nativeDialogRetriesRemaining = 0;
        _timeout.Stop();
        _nativeDialogRetryTimer.Stop();
        _titleLabel.Text = "MOD VOTE";
        _modInfoLabel.Text = "";
        Hide();
        MouseFilter = MouseFilterEnum.Pass;
    }

    private void OnGuiInput(InputEvent @event)
    {
        if (!Visible || _currentVoteModId == null)
            return;

        if (@event is InputEventAction actionEvent && actionEvent.Pressed && actionEvent.Action == "ui_cancel")
        {
            SubmitVote(false);
            AcceptEvent();
        }
    }

    private void OnNativeDialogCompleted(bool voteYes)
    {
        if (_currentVoteModId == null)
            return;

        _usingNativeDialog = false;
        var id = _currentVoteModId;
        _onVoteComplete?.Invoke(id, voteYes);
        Reset();
    }

    private bool TryShowNativeDialog()
    {
        if (!NativeDialogBridge.TryShow(
                GetTree(),
                _pendingTitle,
                _pendingBody,
                "YES",
                "NO",
                hideCancelButton: false,
                onComplete: OnNativeDialogCompleted))
        {
            return false;
        }

        _usingNativeDialog = true;
        MouseFilter = MouseFilterEnum.Pass;
        _nativeDialogRetryTimer.Stop();
        return true;
    }

    private void OnNativeDialogRetryTimeout()
    {
        if (_currentVoteModId == null)
            return;

        if (TryShowNativeDialog())
            return;

        _nativeDialogRetriesRemaining--;
        if (_nativeDialogRetriesRemaining > 0)
        {
            _nativeDialogRetryTimer.Start();
            return;
        }

        _titleLabel.Text = _pendingTitle;
        _modInfoLabel.Text = _pendingBody;
        Show();
        MouseFilter = MouseFilterEnum.Stop;
        _yesBtn.CallDeferred("grab_focus");
    }
}
