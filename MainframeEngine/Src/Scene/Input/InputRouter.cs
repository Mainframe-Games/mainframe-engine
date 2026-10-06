using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// Turns Silk.NET input callbacks (keyboards, mice, gamepads; devices may connect later) into
/// <see cref="InputEvent"/>s pushed through a <see cref="SceneTree"/>. One reused event instance per type keeps
/// input allocation-free. Owned by <see cref="Engine"/>.
/// </summary>
internal sealed class InputRouter : IDisposable
{
    private readonly IInputContext _input;
    private readonly SceneTree _tree;
    private readonly InputEventKey _key = new();
    private readonly InputEventText _text = new();
    private readonly InputEventMouseButton _mouseButton = new();
    private readonly InputEventMouseMotion _mouseMotion = new();
    private readonly InputEventMouseWheel _mouseWheel = new();
    private readonly InputEventGamepadButton _gamepadButton = new();
    private readonly InputEventGamepadAxis _gamepadAxis = new();
    private readonly List<IInputDevice> _attached = [];
    private Vector2? _lastMousePosition;

    public InputRouter(IInputContext input, SceneTree tree)
    {
        _input = input;
        _tree = tree;
        foreach (var keyboard in input.Keyboards)
            Attach(keyboard);
        foreach (var mouse in input.Mice)
            Attach(mouse);
        foreach (var gamepad in input.Gamepads)
            Attach(gamepad);
        input.ConnectionChanged += OnConnectionChanged;
    }

    private void OnConnectionChanged(IInputDevice device, bool connected)
    {
        if (connected)
            Attach(device);
        else
            Detach(device);
    }

    private void Attach(IInputDevice device)
    {
        if (_attached.Contains(device))
            return;
        switch (device)
        {
            case IKeyboard keyboard:
                keyboard.KeyDown += OnKeyDown;
                keyboard.KeyUp += OnKeyUp;
                keyboard.KeyChar += OnKeyChar;
                break;
            case IMouse mouse:
                mouse.MouseDown += OnMouseDown;
                mouse.MouseUp += OnMouseUp;
                mouse.MouseMove += OnMouseMove;
                mouse.Scroll += OnScroll;
                break;
            case IGamepad gamepad:
                gamepad.ButtonDown += OnGamepadButtonDown;
                gamepad.ButtonUp += OnGamepadButtonUp;
                gamepad.ThumbstickMoved += OnThumbstickMoved;
                gamepad.TriggerMoved += OnTriggerMoved;
                break;
            default:
                return;
        }

        _attached.Add(device);
    }

    private void Detach(IInputDevice device)
    {
        if (!_attached.Remove(device))
            return;
        switch (device)
        {
            case IKeyboard keyboard:
                keyboard.KeyDown -= OnKeyDown;
                keyboard.KeyUp -= OnKeyUp;
                keyboard.KeyChar -= OnKeyChar;
                break;
            case IMouse mouse:
                mouse.MouseDown -= OnMouseDown;
                mouse.MouseUp -= OnMouseUp;
                mouse.MouseMove -= OnMouseMove;
                mouse.Scroll -= OnScroll;
                break;
            case IGamepad gamepad:
                gamepad.ButtonDown -= OnGamepadButtonDown;
                gamepad.ButtonUp -= OnGamepadButtonUp;
                gamepad.ThumbstickMoved -= OnThumbstickMoved;
                gamepad.TriggerMoved -= OnTriggerMoved;
                break;
        }
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode) => PushKey(key, scancode, true);

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode) => PushKey(key, scancode, false);

    private void PushKey(Key key, int scancode, bool pressed)
    {
        _key.Key = key;
        _key.Scancode = scancode;
        _key.Pressed = pressed;
        _tree.PushInput(_key);
    }

    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        _text.Character = character;
        _tree.PushInput(_text);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button) => PushMouseButton(mouse, button, true);

    private void OnMouseUp(IMouse mouse, MouseButton button) => PushMouseButton(mouse, button, false);

    private void PushMouseButton(IMouse mouse, MouseButton button, bool pressed)
    {
        _mouseButton.Button = button;
        _mouseButton.Pressed = pressed;
        _mouseButton.Position = mouse.Position;
        _tree.PushInput(_mouseButton);
    }

    private void OnMouseMove(IMouse mouse, Vector2 position)
    {
        _mouseMotion.Relative = _lastMousePosition is { } last ? position - last : Vector2.Zero;
        _mouseMotion.Position = position;
        _lastMousePosition = position;
        _tree.PushInput(_mouseMotion);
    }

    /// <summary>
    /// Applies <paramref name="mode"/> to every mouse (ADR 0125): Captured → Silk's Raw (SDL relative mode, whose
    /// reported position accumulates the relative motion), Hidden → Hidden, otherwise Normal. The next motion event
    /// reports no relative motion, so switching modes never jumps the camera.
    /// </summary>
    public void ApplyMouseMode(MouseMode mode)
    {
        var cursorMode = mode switch
        {
            MouseMode.Captured => CursorMode.Raw,
            MouseMode.Hidden or MouseMode.ConfinedHidden => CursorMode.Hidden,
            _ => CursorMode.Normal,
        };
        foreach (var mouse in _input.Mice)
            mouse.Cursor.CursorMode = cursorMode;
        _lastMousePosition = null;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        _mouseWheel.Delta = new Vector2(wheel.X, wheel.Y);
        _tree.PushInput(_mouseWheel);
    }

    private void OnGamepadButtonDown(IGamepad gamepad, Button button) => PushGamepadButton(gamepad, button, true);

    private void OnGamepadButtonUp(IGamepad gamepad, Button button) => PushGamepadButton(gamepad, button, false);

    private void PushGamepadButton(IGamepad gamepad, Button button, bool pressed)
    {
        _gamepadButton.Device = gamepad.Index;
        _gamepadButton.Button = button.Name;
        _gamepadButton.Pressed = pressed;
        _tree.PushInput(_gamepadButton);
    }

    private void OnThumbstickMoved(IGamepad gamepad, Thumbstick stick)
    {
        _gamepadAxis.Device = gamepad.Index;
        _gamepadAxis.Axis = stick.Index == 0 ? GamepadAxis.LeftStick : GamepadAxis.RightStick;
        _gamepadAxis.Value = new Vector2(stick.X, stick.Y);
        _tree.PushInput(_gamepadAxis);
    }

    private void OnTriggerMoved(IGamepad gamepad, Trigger trigger)
    {
        _gamepadAxis.Device = gamepad.Index;
        _gamepadAxis.Axis = trigger.Index == 0 ? GamepadAxis.LeftTrigger : GamepadAxis.RightTrigger;
        _gamepadAxis.Value = new Vector2(trigger.Position, 0);
        _tree.PushInput(_gamepadAxis);
    }

    public void Dispose()
    {
        _input.ConnectionChanged -= OnConnectionChanged;
        foreach (var device in _attached.ToArray())
            Detach(device);
    }
}
