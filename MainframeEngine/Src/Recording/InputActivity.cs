using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// Classifies input events for the recording sidecar: key, mouse and gamepad buttons, mouse motion over
/// 2 px and a stick or trigger beyond 0.25 count as player input. Pressed buttons and deflected axes are
/// "held" until released, so walking with W held is never mistaken for idling. The recorder's own
/// threshold, independent of <see cref="InputAction.Deadzone"/>.
/// </summary>
internal sealed class InputActivity
{
    private const float MotionPixels = 2f;
    private const float AxisThreshold = 0.25f;

    private readonly HashSet<Key> _keys = [];
    private readonly HashSet<MouseButton> _mouse = [];
    private readonly HashSet<(int Device, ButtonName Button)> _pads = [];
    private readonly HashSet<(int Device, GamepadAxis Axis)> _axes = [];

    public bool IsHeld => _keys.Count + _mouse.Count + _pads.Count + _axes.Count > 0;

    /// <summary>Updates held state; true when <paramref name="e"/> is player input.</summary>
    public bool Observe(InputEvent e)
    {
        switch (e)
        {
            case InputEventKey key:
                Track(_keys, key.Key, key.Pressed);
                return true;
            case InputEventMouseButton button:
                Track(_mouse, button.Button, button.Pressed);
                return true;
            case InputEventGamepadButton pad:
                Track(_pads, (pad.Device, pad.Button), pad.Pressed);
                return true;
            case InputEventMouseMotion motion:
                return motion.Relative.Length() > MotionPixels;
            case InputEventMouseWheel:
                return true;
            case InputEventGamepadAxis axis:
                var deflected = axis.Value.Length() > AxisThreshold;
                var wasDeflected = _axes.Contains((axis.Device, axis.Axis));
                Track(_axes, (axis.Device, axis.Axis), deflected);
                return deflected || wasDeflected;
            default:
                return false; // actions are synthesised from the events above; text duplicates keys
        }
    }

    private static void Track<T>(HashSet<T> set, T item, bool down)
    {
        if (down)
            set.Add(item);
        else
            set.Remove(item);
    }
}
