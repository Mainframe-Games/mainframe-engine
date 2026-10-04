using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// Base of the input events routed through the tree (<see cref="SceneTree.PushInput"/> →
/// <see cref="Node.OnInput"/> / <see cref="Node.OnUnhandledInput"/>).
/// </summary>
/// <remarks>
/// The engine reuses one instance per event type to keep input allocation-free: read what you need inside the
/// callback and do not keep the event (copy it with <see cref="Clone"/> if you must).
/// </remarks>
public abstract class InputEvent
{
    /// <summary>A copy that is safe to keep after the callback returns.</summary>
    public InputEvent Clone() => (InputEvent)MemberwiseClone();
}

/// <summary>A key went down, repeated or up.</summary>
public sealed class InputEventKey : InputEvent
{
    public Key Key { get; set; }

    /// <summary>Platform scancode.</summary>
    public int Scancode { get; set; }

    public bool Pressed { get; set; }

    public override string ToString() => $"Key {Key} {(Pressed ? "down" : "up")}";
}

/// <summary>A mouse button went down or up.</summary>
public sealed class InputEventMouseButton : InputEvent
{
    public MouseButton Button { get; set; }

    public bool Pressed { get; set; }

    /// <summary>Cursor position in window coordinates.</summary>
    public Vector2 Position { get; set; }

    public override string ToString() => $"Mouse {Button} {(Pressed ? "down" : "up")} at {Position}";
}

/// <summary>The mouse moved.</summary>
public sealed class InputEventMouseMotion : InputEvent
{
    /// <summary>Cursor position in window coordinates.</summary>
    public Vector2 Position { get; set; }

    /// <summary>Movement since the previous motion event.</summary>
    public Vector2 Relative { get; set; }

    public override string ToString() => $"Mouse motion to {Position} ({Relative})";
}

/// <summary>The mouse wheel (or a touchpad) scrolled.</summary>
public sealed class InputEventMouseWheel : InputEvent
{
    public Vector2 Delta { get; set; }

    public override string ToString() => $"Mouse wheel {Delta}";
}

/// <summary>A character was typed (text input, after keyboard layout and IME).</summary>
public sealed class InputEventText : InputEvent
{
    public char Character { get; set; }

    public override string ToString() => $"Text '{Character}'";
}

/// <summary>A gamepad button went down or up.</summary>
public sealed class InputEventGamepadButton : InputEvent
{
    public int Device { get; set; }

    public ButtonName Button { get; set; }

    public bool Pressed { get; set; }

    public override string ToString() => $"Gamepad {Device} {Button} {(Pressed ? "down" : "up")}";
}

/// <summary>A gamepad stick or trigger moved.</summary>
public sealed class InputEventGamepadAxis : InputEvent
{
    public int Device { get; set; }

    /// <summary>Which control: <see cref="GamepadAxis"/>.</summary>
    public GamepadAxis Axis { get; set; }

    /// <summary>Stick position (-1..1 per axis) or trigger value in <c>X</c> (0..1).</summary>
    public Vector2 Value { get; set; }

    public override string ToString() => $"Gamepad {Device} {Axis} {Value}";
}

/// <summary>The gamepad control an <see cref="InputEventGamepadAxis"/> reports.</summary>
public enum GamepadAxis
{
    LeftStick,
    RightStick,
    LeftTrigger,
    RightTrigger,
}
