using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// Polled input for one <see cref="SceneTree"/> (<see cref="SceneTree.Input"/>; the engine's tree is also reachable
/// through the static <see cref="Input"/>): which keys, buttons and <see cref="InputMap"/> actions are held, their
/// strengths, and whether they were pressed or released this frame. Fed by every event pushed through the tree
/// (<see cref="SceneTree.PushInput"/>), before the UI or nodes see it, so a UI that consumes a click does not leave
/// an action stuck.
/// </summary>
/// <remarks>
/// "Just pressed" follows Godot: in <see cref="Node.OnProcess"/> it is true for the process frame after the press;
/// in <see cref="Node.OnPhysicsProcess"/> for the first physics step after it. Queries and event handling do not
/// allocate (the action table is rebuilt only when the <see cref="Map"/> changes). Main thread only.
/// </remarks>
/// <summary>How the OS cursor behaves (Godot's <c>Input.MouseMode</c>, same ordinals; ADR 0125).</summary>
public enum MouseMode
{
    /// <summary>Shown and free (the default).</summary>
    Visible,

    /// <summary>Hidden over the window, otherwise free.</summary>
    Hidden,

    /// <summary>Hidden, held in the window, reporting relative motion only: mouse look.</summary>
    Captured,

    /// <summary>Shown and kept inside the window. Not implemented yet: behaves like <see cref="Visible"/>.</summary>
    Confined,

    /// <summary>Hidden and kept inside the window. Not implemented yet: behaves like <see cref="Hidden"/>.</summary>
    ConfinedHidden,
}

public sealed class InputState
{
    /// <summary>Gamepads tracked by index (0..MaxGamepads-1); events from higher indices are ignored.</summary>
    public const int MaxGamepads = 8;

    private const ulong Never = ulong.MaxValue;
    private static readonly int KeyCount = MaxEnumValue<Key>() + 1;
    private static readonly int MouseButtonCount = MaxEnumValue<MouseButton>() + 1;
    private static readonly int GamepadButtonCount = MaxEnumValue<ButtonName>() + 1;
    private static readonly int GamepadAxisCount = MaxEnumValue<GamepadAxisCode>() + 1;

    private readonly SceneTree _tree;
    private readonly bool[] _keys = new bool[KeyCount];
    private readonly bool[] _mouseButtons = new bool[MouseButtonCount];
    private readonly bool[] _padButtons = new bool[MaxGamepads * GamepadButtonCount];
    private readonly float[] _padAxes = new float[MaxGamepads * GamepadAxisCount];
    private InputMap _map = new();
    private int _builtVersion = -1;
    private ActionState[] _states = [];

    private struct ActionState
    {
        public float Strength;
        public float ForcedStrength;
        public bool Pressed;
        public ulong PressedProcessFrame;
        public ulong ReleasedProcessFrame;
        public ulong PressedPhysicsFrame;
        public ulong ReleasedPhysicsFrame;
    }

    internal InputState(SceneTree tree)
    {
        _tree = tree;
    }

    /// <summary>The tree this state belongs to.</summary>
    internal SceneTree Tree => _tree;

    /// <summary>The actions; replace it (or edit it) at any time. Defaults to an empty map.</summary>
    public InputMap Map
    {
        get => _map;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(value, _map))
                return;
            _map = value;
            _builtVersion = -1;
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------------------------------------

    /// <summary>True while any of <paramref name="action"/>'s inputs is held (axes: past the deadzone).</summary>
    public bool IsActionPressed(string action) => TryGetState(action, out var index) && _states[index].Pressed;

    /// <summary>True in the frame (or first physics step) after <paramref name="action"/> was pressed.</summary>
    public bool IsActionJustPressed(string action)
    {
        if (!TryGetState(action, out var index))
            return false;
        ref var state = ref _states[index];
        return state.Pressed && (_tree.IsInPhysicsStep
            ? state.PressedPhysicsFrame == _tree.PhysicsFrames
            : state.PressedProcessFrame == _tree.ProcessFrames);
    }

    /// <summary>True in the frame (or first physics step) after <paramref name="action"/> was released.</summary>
    public bool IsActionJustReleased(string action)
    {
        if (!TryGetState(action, out var index))
            return false;
        ref var state = ref _states[index];
        return !state.Pressed && (_tree.IsInPhysicsStep
            ? state.ReleasedPhysicsFrame == _tree.PhysicsFrames
            : state.ReleasedProcessFrame == _tree.ProcessFrames);
    }

    /// <summary>0..1: 1 for a held key or button, the deadzone-rescaled value for axes, 0 when released or unknown.</summary>
    public float GetActionStrength(string action) => TryGetState(action, out var index) ? _states[index].Strength : 0f;

    /// <summary><c>Strength(positive) - Strength(negative)</c>, in -1..1.</summary>
    public float GetAxis(string negative, string positive) => GetActionStrength(positive) - GetActionStrength(negative);

    /// <summary>A 2D direction from four actions, its length clamped to 1 (e.g. move left/right/up/down).</summary>
    public Vector2 GetVector(string negativeX, string positiveX, string negativeY, string positiveY)
    {
        var vector = new Vector2(GetAxis(negativeX, positiveX), GetAxis(negativeY, positiveY));
        var length = vector.Length();
        return length > 1f ? vector / length : vector;
    }

    /// <summary>Presses <paramref name="action"/> as if an input did (tests, on-screen buttons, replays).</summary>
    public void ActionPress(string action, float strength = 1f)
    {
        if (!TryGetState(action, out var index))
            return;
        _states[index].ForcedStrength = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f;
        Refresh(index);
    }

    /// <summary>Releases a press made with <see cref="ActionPress"/> (held physical inputs stay pressed).</summary>
    public void ActionRelease(string action)
    {
        if (!TryGetState(action, out var index))
            return;
        _states[index].ForcedStrength = 0f;
        Refresh(index);
    }

    /// <summary>Releases everything (keys, buttons, axes, forced actions), e.g. when the window loses focus.</summary>
    public void ReleaseAll()
    {
        Array.Clear(_keys);
        Array.Clear(_mouseButtons);
        Array.Clear(_padButtons);
        Array.Clear(_padAxes);
        EnsureBuilt();
        for (var i = 0; i < _states.Length; i++)
        {
            _states[i].ForcedStrength = 0f;
            Refresh(i);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Raw devices
    // ------------------------------------------------------------------------------------------------

    public bool IsKeyPressed(Key key) => (uint)key < (uint)KeyCount && _keys[(int)key];

    public bool IsMouseButtonPressed(MouseButton button) => (uint)button < (uint)MouseButtonCount && _mouseButtons[(int)button];

    public bool IsGamepadButtonPressed(int device, ButtonName button) =>
        (uint)device < MaxGamepads && (uint)button < (uint)GamepadButtonCount && _padButtons[device * GamepadButtonCount + (int)button];

    /// <summary>The pointer's last position in window points (from mouse motion and button events).</summary>
    public Vector2 MousePosition { get; private set; }

    /// <summary>
    /// How the OS cursor behaves (Godot's <c>Input.mouse_mode</c>, ADR 0125). <see cref="MouseMode.Captured"/> hides it,
    /// keeps it in the window and reports unbounded relative motion (<see cref="InputEventMouseMotion.Relative"/>), for
    /// mouse look; the game UI ignores the mouse meanwhile. The engine applies the mode to the window's mice.
    /// </summary>
    public MouseMode MouseMode
    {
        get => _mouseMode;
        set
        {
            if (_mouseMode == value)
                return;
            _mouseMode = value;
            MouseModeChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when <see cref="MouseMode"/> changes; the engine sets the OS cursor mode from it.</summary>
    internal Action<MouseMode>? MouseModeChanged { get; set; }

    private MouseMode _mouseMode;

    /// <summary>Raw axis value (sticks -1..1 with Y positive down, triggers 0..1).</summary>
    public float GetGamepadAxis(int device, GamepadAxisCode axis) =>
        (uint)device < MaxGamepads && (uint)axis < (uint)GamepadAxisCount ? _padAxes[device * GamepadAxisCount + (int)axis] : 0f;

    // ------------------------------------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------------------------------------

    /// <summary>Updates device and action state from <paramref name="inputEvent"/> (called by <see cref="SceneTree.PushInput"/>).</summary>
    public void ProcessEvent(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        EnsureBuilt(); // before the device state changes, so this event produces a "just pressed" edge
        switch (inputEvent)
        {
            case InputEventKey key:
                if ((uint)key.Key < (uint)KeyCount)
                    _keys[(int)key.Key] = key.Pressed;
                break;
            case InputEventMouseButton mouse:
                MousePosition = mouse.Position;
                if ((uint)mouse.Button < (uint)MouseButtonCount)
                    _mouseButtons[(int)mouse.Button] = mouse.Pressed;
                break;
            case InputEventMouseMotion motion:
                MousePosition = motion.Position;
                return;
            case InputEventAction byCode:
                if (byCode.Pressed)
                    ActionPress(byCode.Action, byCode.Strength);
                else
                    ActionRelease(byCode.Action);
                return;
            case InputEventGamepadButton pad:
                if ((uint)pad.Device < MaxGamepads && (uint)pad.Button < (uint)GamepadButtonCount)
                    _padButtons[pad.Device * GamepadButtonCount + (int)pad.Button] = pad.Pressed;
                break;
            case InputEventGamepadAxis axis:
                if ((uint)axis.Device < MaxGamepads)
                    StoreAxis(axis);
                break;
            default:
                return; // motion, wheel and text never drive actions
        }

        var actions = _map.Actions;
        for (var i = 0; i < actions.Count; i++)
        {
            if (IsAffectedBy(actions[i], inputEvent))
                Refresh(i);
        }
    }

    private void StoreAxis(InputEventGamepadAxis axis)
    {
        var baseIndex = axis.Device * GamepadAxisCount;
        switch (axis.Axis)
        {
            case MainframeEngine.GamepadAxis.LeftStick:
                _padAxes[baseIndex + (int)GamepadAxisCode.LeftX] = axis.Value.X;
                _padAxes[baseIndex + (int)GamepadAxisCode.LeftY] = axis.Value.Y;
                break;
            case MainframeEngine.GamepadAxis.RightStick:
                _padAxes[baseIndex + (int)GamepadAxisCode.RightX] = axis.Value.X;
                _padAxes[baseIndex + (int)GamepadAxisCode.RightY] = axis.Value.Y;
                break;
            case MainframeEngine.GamepadAxis.LeftTrigger:
                _padAxes[baseIndex + (int)GamepadAxisCode.LeftTrigger] = axis.Value.X;
                break;
            case MainframeEngine.GamepadAxis.RightTrigger:
                _padAxes[baseIndex + (int)GamepadAxisCode.RightTrigger] = axis.Value.X;
                break;
        }
    }

    /// <summary>The value of <paramref name="code"/> carried by <paramref name="axis"/>, if it carries it.</summary>
    internal static bool AxisValue(InputEventGamepadAxis axis, GamepadAxisCode code, out float value)
    {
        value = 0f;
        switch (axis.Axis, code)
        {
            case (MainframeEngine.GamepadAxis.LeftStick, GamepadAxisCode.LeftX):
            case (MainframeEngine.GamepadAxis.RightStick, GamepadAxisCode.RightX):
            case (MainframeEngine.GamepadAxis.LeftTrigger, GamepadAxisCode.LeftTrigger):
            case (MainframeEngine.GamepadAxis.RightTrigger, GamepadAxisCode.RightTrigger):
                value = axis.Value.X;
                return true;
            case (MainframeEngine.GamepadAxis.LeftStick, GamepadAxisCode.LeftY):
            case (MainframeEngine.GamepadAxis.RightStick, GamepadAxisCode.RightY):
                value = axis.Value.Y;
                return true;
            default:
                return false;
        }
    }

    private static bool IsAffectedBy(InputAction action, InputEvent inputEvent)
    {
        foreach (var binding in action.Bindings)
        {
            var affected = inputEvent switch
            {
                InputEventKey key => binding.Kind == InputBindingKind.Key && binding.Code == (int)key.Key,
                InputEventMouseButton mouse => binding.Kind == InputBindingKind.MouseButton && binding.Code == (int)mouse.Button,
                InputEventGamepadButton pad => binding.Kind == InputBindingKind.GamepadButton && binding.Code == (int)pad.Button
                                               && binding.MatchesDevice(pad.Device),
                InputEventGamepadAxis axis => binding.Kind == InputBindingKind.GamepadAxis && binding.MatchesDevice(axis.Device)
                                              && AxisValue(axis, (GamepadAxisCode)binding.Code, out _),
                _ => false,
            };
            if (affected)
                return true;
        }

        return false;
    }

    // Recomputes action i's strength from the device state and stamps press/release transitions.
    private void Refresh(int index)
    {
        ref var state = ref _states[index];
        var action = _map.Actions[index];
        var strength = state.ForcedStrength;
        foreach (var binding in action.Bindings)
        {
            var s = Evaluate(action, binding);
            if (s > strength)
                strength = s;
        }

        state.Strength = strength;
        var pressed = strength > 0f;
        if (pressed == state.Pressed)
            return;
        state.Pressed = pressed;
        if (pressed)
        {
            state.PressedProcessFrame = _tree.ProcessFrames;
            state.PressedPhysicsFrame = _tree.PhysicsFrames;
        }
        else
        {
            state.ReleasedProcessFrame = _tree.ProcessFrames;
            state.ReleasedPhysicsFrame = _tree.PhysicsFrames;
        }
    }

    private float Evaluate(InputAction action, InputBinding binding)
    {
        switch (binding.Kind)
        {
            case InputBindingKind.Key:
                return (uint)binding.Code < (uint)KeyCount && _keys[binding.Code] ? 1f : 0f;
            case InputBindingKind.MouseButton:
                return (uint)binding.Code < (uint)MouseButtonCount && _mouseButtons[binding.Code] ? 1f : 0f;
            case InputBindingKind.GamepadButton:
                if ((uint)binding.Code >= (uint)GamepadButtonCount)
                    return 0f;
                for (var device = 0; device < MaxGamepads; device++)
                    if (binding.MatchesDevice(device) && _padButtons[device * GamepadButtonCount + binding.Code])
                        return 1f;
                return 0f;
            case InputBindingKind.GamepadAxis:
                {
                    if ((uint)binding.Code >= (uint)GamepadAxisCount)
                        return 0f;
                    var best = 0f;
                    for (var device = 0; device < MaxGamepads; device++)
                    {
                        if (!binding.MatchesDevice(device))
                            continue;
                        var s = action.AxisStrength(_padAxes[device * GamepadAxisCount + binding.Code], binding.Direction);
                        if (s > best)
                            best = s;
                    }

                    return best;
                }
            default:
                return 0f;
        }
    }

    private bool TryGetState(string action, out int index)
    {
        EnsureBuilt();
        index = _map.IndexOf(action);
        return index >= 0;
    }

    private void EnsureBuilt()
    {
        if (_builtVersion == _map.Version)
            return;
        _builtVersion = _map.Version;
        var count = _map.Actions.Count;
        if (_states.Length != count)
            _states = new ActionState[count];
        else
            Array.Clear(_states);
        for (var i = 0; i < count; i++)
        {
            ref var state = ref _states[i];
            state.PressedProcessFrame = state.ReleasedProcessFrame = Never;
            state.PressedPhysicsFrame = state.ReleasedPhysicsFrame = Never;
            // Inputs already held keep the action pressed, without a "just pressed" edge.
            var strength = 0f;
            foreach (var binding in _map.Actions[i].Bindings)
                strength = Math.Max(strength, Evaluate(_map.Actions[i], binding));
            state.Strength = strength;
            state.Pressed = strength > 0f;
        }
    }

    private static int MaxEnumValue<T>() where T : struct, Enum
    {
        var max = 0;
        foreach (var value in Enum.GetValues<T>())
            max = Math.Max(max, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
        return max;
    }
}

/// <summary>
/// Godot-style global input for the running game: <c>Input.IsActionPressed("jump")</c>. Forwards to
/// <see cref="Current"/> — the engine sets it to its tree's <see cref="SceneTree.Input"/> — and answers
/// false / 0 when there is none.
/// </summary>
public static class Input
{
    /// <summary>The state queried by the static methods (the engine's tree's; null outside a running engine).</summary>
    public static InputState? Current { get; set; }

    /// <summary>The current state's actions (null outside a running engine).</summary>
    public static InputMap? Map => Current?.Map;

    public static bool IsActionPressed(string action) => Current?.IsActionPressed(action) ?? false;

    public static bool IsActionJustPressed(string action) => Current?.IsActionJustPressed(action) ?? false;

    public static bool IsActionJustReleased(string action) => Current?.IsActionJustReleased(action) ?? false;

    public static float GetActionStrength(string action) => Current?.GetActionStrength(action) ?? 0f;

    public static float GetAxis(string negative, string positive) => Current?.GetAxis(negative, positive) ?? 0f;

    public static Vector2 GetVector(string negativeX, string positiveX, string negativeY, string positiveY) =>
        Current?.GetVector(negativeX, positiveX, negativeY, positiveY) ?? Vector2.Zero;

    public static bool IsKeyPressed(Key key) => Current?.IsKeyPressed(key) ?? false;

    public static bool IsMouseButtonPressed(MouseButton button) => Current?.IsMouseButtonPressed(button) ?? false;

    /// <summary>A gamepad axis's raw value (Godot's <c>Input.get_joy_axis</c>; sticks −1..1 with Y down, triggers 0..1).</summary>
    public static float GetJoyAxis(int device, GamepadAxisCode axis) => Current?.GetGamepadAxis(device, axis) ?? 0f;

    /// <summary>The pointer's last position in window points.</summary>
    public static Vector2 MousePosition => Current?.MousePosition ?? Vector2.Zero;

    /// <summary>The cursor mode (Godot's <c>Input.mouse_mode</c>; see <see cref="InputState.MouseMode"/>). Visible outside a running engine.</summary>
    public static MouseMode MouseMode
    {
        get => Current?.MouseMode ?? MouseMode.Visible;
        set
        {
            if (Current is { } state)
                state.MouseMode = value;
        }
    }

    public static void ActionPress(string action, float strength = 1f) => Current?.ActionPress(action, strength);

    public static void ActionRelease(string action) => Current?.ActionRelease(action);

    /// <summary>
    /// Feeds an event to the current tree as if a device sent it (Godot's <c>Input.parse_input_event</c>): a copy is
    /// pushed at the end of this frame (deferred calls), in call order.
    /// </summary>
    public static void ParseInputEvent(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        if (Current?.Tree is not { } tree)
            return;
        tree.CallDeferred(static state =>
        {
            var (t, e) = ((SceneTree, InputEvent))state!;
            t.PushInput(e);
        }, (tree, inputEvent.Clone()));
    }
}
