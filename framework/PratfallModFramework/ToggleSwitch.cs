using Godot;

namespace PratfallModFramework;

public class ToggleSwitch : Control
{
    private ColorRect _focusHalo = null!;
    private ColorRect _track = null!;
    private ColorRect _knob = null!;
    private bool _isOn;
    private static readonly Color FocusColor = new(0.98f, 0.92f, 0.44f, 0.45f);
    private static readonly Color ColorOn = new(0.15f, 0.8f, 0.15f);
    private static readonly Color ColorOff = new(0.8f, 0.15f, 0.15f);
    private static readonly Color KnobColor = new(0.95f, 0.95f, 0.95f);
    private const float TrackWidth = 56f;
    private const float TrackHeight = 28f;
    private const float KnobSize = 20f;
    private const float KnobPadding = 4f;
    private const float FocusPadding = 4f;

    public bool IsOn
    {
        get => _isOn;
        set { _isOn = value; UpdateVisuals(); }
    }

    public event System.Action<bool>? Toggled;

    public ToggleSwitch(bool initialState = false)
    {
        CustomMinimumSize = new Vector2(TrackWidth, TrackHeight);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _isOn = initialState;

        _focusHalo = new ColorRect
        {
            Color = FocusColor,
            Position = new Vector2(-FocusPadding, -FocusPadding),
            Size = new Vector2(TrackWidth + (FocusPadding * 2f), TrackHeight + (FocusPadding * 2f)),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
        };
        AddChild(_focusHalo);

        _track = new ColorRect
        {
            Color = initialState ? ColorOn : ColorOff,
            Size = new Vector2(TrackWidth, TrackHeight),
            Position = Vector2.Zero,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_track);

        _knob = new ColorRect
        {
            Color = KnobColor,
            Size = new Vector2(KnobSize, KnobSize),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_knob);

        GuiInput += OnGuiInput;
        FocusEntered += UpdateFocusVisuals;
        FocusExited += UpdateFocusVisuals;
        UpdateVisuals();
    }

    private void OnGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            SetPressedState(!_isOn);
            GrabFocus();
            AcceptEvent();
            return;
        }

        if (@event is InputEventAction action && action.Pressed && action.Action == "ui_accept")
        {
            SetPressedState(!_isOn);
            AcceptEvent();
        }
    }

    private void SetPressedState(bool isOn)
    {
        _isOn = isOn;
        Toggled?.Invoke(_isOn);
        UpdateVisuals();
    }

    private void UpdateVisuals()
    {
        _track.Color = _isOn ? ColorOn : ColorOff;
        var knobX = _isOn
            ? TrackWidth - KnobSize - KnobPadding
            : KnobPadding;
        _knob.Position = new Vector2(knobX, (TrackHeight - KnobSize) * 0.5f);
        UpdateFocusVisuals();
    }

    private void UpdateFocusVisuals()
    {
        _focusHalo.Visible = HasFocus();
    }
}
