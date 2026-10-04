using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>What an <see cref="InputBinding"/> listens to.</summary>
public enum InputBindingKind : byte
{
    None,
    Key,
    MouseButton,
    GamepadButton,
    GamepadAxis,
}

/// <summary>One gamepad axis direction source (stick axes are raw SDL values: Y is positive <b>down</b>).</summary>
public enum GamepadAxisCode : byte
{
    LeftX,
    LeftY,
    RightX,
    RightY,
    LeftTrigger,
    RightTrigger,
}

/// <summary>
/// One physical input that drives an <see cref="InputAction"/>: a key, a mouse button, a gamepad button, or one
/// direction of a gamepad axis. Gamepad bindings match every pad (<see cref="Device"/> = -1) or one pad index. The text
/// form, used in <c>project.mfproj</c>, is <c>key:Space</c>, <c>mouse:Left</c>, <c>pad:A</c>, <c>pad1:Start</c>,
/// <c>axis:LeftX-</c>, <c>axis:RightTrigger+</c>.
/// </summary>
public readonly record struct InputBinding
{
    private InputBinding(InputBindingKind kind, int code, sbyte direction, int device)
    {
        Kind = kind;
        Code = code;
        Direction = direction;
        Device = device;
    }

    public InputBindingKind Kind { get; }

    /// <summary>The <see cref="Silk.NET.Input.Key"/>, <see cref="Silk.NET.Input.MouseButton"/>,
    /// <see cref="ButtonName"/> or <see cref="GamepadAxisCode"/> value.</summary>
    public int Code { get; }

    /// <summary>For axes: +1 or -1, the direction that counts as pressing the action. 0 otherwise.</summary>
    public sbyte Direction { get; }

    /// <summary>Gamepad index, or -1 for any pad. -1 for keys and mouse buttons.</summary>
    public int Device { get; }

    public static InputBinding Key(Key key) => new(InputBindingKind.Key, (int)key, 0, -1);

    public static InputBinding Mouse(MouseButton button) => new(InputBindingKind.MouseButton, (int)button, 0, -1);

    public static InputBinding GamepadButton(ButtonName button, int device = -1) =>
        new(InputBindingKind.GamepadButton, (int)button, 0, CheckDevice(device));

    /// <summary>One direction (<paramref name="direction"/> &gt; 0: positive values) of a gamepad axis.</summary>
    public static InputBinding GamepadAxis(GamepadAxisCode axis, int direction, int device = -1) =>
        new(InputBindingKind.GamepadAxis, (int)axis, direction < 0 ? (sbyte)-1 : (sbyte)1, CheckDevice(device));

    // -1 (or any negative) = every pad; otherwise a pad InputState tracks (0..MaxGamepads-1).
    private static int CheckDevice(int device)
    {
        if (device >= InputState.MaxGamepads)
            throw new ArgumentOutOfRangeException(nameof(device), device, $"Gamepad indices are 0..{InputState.MaxGamepads - 1} (or -1 for any).");
        return Math.Max(-1, device);
    }

    /// <summary>True when the binding listens to a gamepad with index <paramref name="device"/>.</summary>
    public bool MatchesDevice(int device) => Device < 0 || Device == device;

    public override string ToString()
    {
        var device = Device >= 0 ? Device.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return Kind switch
        {
            InputBindingKind.Key => "key:" + (Key)Code,
            InputBindingKind.MouseButton => "mouse:" + (MouseButton)Code,
            InputBindingKind.GamepadButton => "pad" + device + ":" + (ButtonName)Code,
            InputBindingKind.GamepadAxis => "axis" + device + ":" + (GamepadAxisCode)Code + (Direction < 0 ? "-" : "+"),
            _ => "none",
        };
    }

    /// <summary>Parses the text form (see the type summary); throws <see cref="FormatException"/> when invalid.</summary>
    public static InputBinding Parse(string text) =>
        TryParse(text, out var binding) ? binding : throw new FormatException($"Invalid input binding '{text}'. Expected key:<Key>, mouse:<Button>, pad[N]:<Button> or axis[N]:<Axis>+/-.");

    public static bool TryParse([NotNullWhen(true)] string? text, out InputBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
            return false;
        var prefix = text.AsSpan(0, colon).Trim();
        var value = text.AsSpan(colon + 1).Trim();

        if (prefix.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseEnum<Key>(value, out var key) || key == Silk.NET.Input.Key.Unknown)
                return false;
            binding = Key(key);
            return true;
        }

        if (prefix.Equals("mouse", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseEnum<MouseButton>(value, out var button) || button == MouseButton.Unknown)
                return false;
            binding = Mouse(button);
            return true;
        }

        if (prefix.StartsWith("pad", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDevice(prefix[3..], out var device) || !TryParseEnum<ButtonName>(value, out var button) || button == ButtonName.Unknown)
                return false;
            binding = GamepadButton(button, device);
            return true;
        }

        if (prefix.StartsWith("axis", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDevice(prefix[4..], out var device) || value.Length < 2)
                return false;
            var sign = value[^1];
            if (sign is not ('+' or '-') || !TryParseEnum<GamepadAxisCode>(value[..^1], out var axis))
                return false;
            binding = GamepadAxis(axis, sign == '-' ? -1 : 1, device);
            return true;
        }

        return false;
    }

    private static bool TryParseDevice(ReadOnlySpan<char> text, out int device)
    {
        device = -1;
        return text.IsEmpty || (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out device) && device < InputState.MaxGamepads);
    }

    private static bool TryParseEnum<T>(ReadOnlySpan<char> text, out T value) where T : struct, Enum =>
        Enum.TryParse(text, ignoreCase: true, out value) && Enum.IsDefined(value) && !IsNumeric(text);

    private static bool IsNumeric(ReadOnlySpan<char> text) => text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '-');
}

/// <summary>A named action (<c>"jump"</c>) and the inputs that trigger it.</summary>
public sealed class InputAction
{
    /// <summary>Default <see cref="Deadzone"/> (Godot's).</summary>
    public const float DefaultDeadzone = 0.5f;

    internal InputAction(string name, float deadzone)
    {
        Name = name;
        Deadzone = deadzone;
    }

    public string Name { get; }

    /// <summary>
    /// Axis values at or below this count as released; above it the strength rescales from 0 to 1. Buttons and keys
    /// are 0 or 1.
    /// </summary>
    public float Deadzone
    {
        get;
        set => field = float.IsFinite(value) ? Math.Clamp(value, 0f, 0.99f) : DefaultDeadzone;
    }

    /// <summary>The inputs that trigger the action (edit through <see cref="InputMap"/> so states refresh).</summary>
    public List<InputBinding> Bindings { get; } = [];

    /// <summary>Strength (0..1) of a raw axis value along this binding's direction, after the deadzone.</summary>
    public float AxisStrength(float value, int direction)
    {
        var along = direction < 0 ? -value : value;
        if (!float.IsFinite(along) || along <= Deadzone)
            return 0f;
        return Math.Clamp((along - Deadzone) / (1f - Deadzone), 0f, 1f);
    }
}

/// <summary>
/// The project's actions (Godot's InputMap): names mapped to keys, mouse buttons, gamepad buttons and axes. Stored in
/// the <c>input</c> section of <c>project.mfproj</c> (<see cref="ProjectSettings.Input"/>); read at runtime through
/// <see cref="Input"/> / <see cref="InputState"/>.
/// </summary>
public sealed class InputMap
{
    private readonly List<InputAction> _actions = [];
    private readonly Dictionary<string, int> _byName = new(StringComparer.Ordinal);

    /// <summary>Actions in definition order.</summary>
    public IReadOnlyList<InputAction> Actions => _actions;

    /// <summary>Changes whenever an action or binding is added or removed (input states rebuild their tables).</summary>
    public int Version { get; private set; }

    public bool HasAction(string name) => _byName.ContainsKey(name);

    public InputAction? GetAction(string name) => _byName.TryGetValue(name, out var index) ? _actions[index] : null;

    /// <summary>Index of <paramref name="name"/> in <see cref="Actions"/>, or -1.</summary>
    public int IndexOf(string name) => _byName.TryGetValue(name, out var index) ? index : -1;

    /// <summary>Adds an action (or returns the existing one with that name, deadzone unchanged).</summary>
    public InputAction AddAction(string name, float deadzone = InputAction.DefaultDeadzone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (GetAction(name) is { } existing)
            return existing;
        var action = new InputAction(name, deadzone);
        _byName[name] = _actions.Count;
        _actions.Add(action);
        Version++;
        return action;
    }

    public bool RemoveAction(string name)
    {
        if (!_byName.Remove(name, out var index))
            return false;
        _actions.RemoveAt(index);
        for (var i = index; i < _actions.Count; i++)
            _byName[_actions[i].Name] = i;
        Version++;
        return true;
    }

    /// <summary>Adds <paramref name="binding"/> to <paramref name="action"/> (created when missing); duplicates are ignored.</summary>
    public InputMap Bind(string action, InputBinding binding)
    {
        if (binding.Kind == InputBindingKind.None)
            throw new ArgumentException("Cannot bind an empty InputBinding.", nameof(binding));
        var target = AddAction(action);
        if (!target.Bindings.Contains(binding))
        {
            target.Bindings.Add(binding);
            Version++;
        }

        return this;
    }

    /// <summary>Adds bindings in their text form (<c>"key:Space"</c>, …).</summary>
    public InputMap Bind(string action, params ReadOnlySpan<string> bindings)
    {
        foreach (var text in bindings)
            Bind(action, InputBinding.Parse(text));
        return this;
    }

    public bool Unbind(string action, InputBinding binding)
    {
        if (GetAction(action) is not { } target || !target.Bindings.Remove(binding))
            return false;
        Version++;
        return true;
    }

    public void Clear()
    {
        _actions.Clear();
        _byName.Clear();
        Version++;
    }

    /// <summary>A deep copy.</summary>
    public InputMap Clone()
    {
        var copy = new InputMap();
        foreach (var action in _actions)
        {
            var target = copy.AddAction(action.Name, action.Deadzone);
            target.Bindings.AddRange(action.Bindings);
        }

        return copy;
    }

    /// <summary>
    /// Whether <paramref name="inputEvent"/> is one of <paramref name="action"/>'s inputs; <paramref name="pressed"/>
    /// and <paramref name="strength"/> describe the event (axes: past the deadzone).
    /// </summary>
    public bool EventMatches(InputEvent inputEvent, string action, out bool pressed, out float strength)
    {
        pressed = false;
        strength = 0f;
        if (inputEvent is null || GetAction(action) is not { } target)
            return false;
        return EventMatches(inputEvent, target, out pressed, out strength);
    }

    internal static bool EventMatches(InputEvent inputEvent, InputAction action, out bool pressed, out float strength)
    {
        pressed = false;
        strength = 0f;
        var matched = false;
        foreach (var binding in action.Bindings)
        {
            if (!Matches(inputEvent, binding, action, out var p, out var s))
                continue;
            matched = true;
            if (s > strength)
                strength = s;
            pressed |= p;
        }

        return matched;
    }

    private static bool Matches(InputEvent inputEvent, InputBinding binding, InputAction action, out bool pressed, out float strength)
    {
        pressed = false;
        strength = 0f;
        switch (inputEvent)
        {
            case InputEventKey key when binding.Kind == InputBindingKind.Key && (int)key.Key == binding.Code:
                pressed = key.Pressed;
                break;
            case InputEventMouseButton mouse when binding.Kind == InputBindingKind.MouseButton && (int)mouse.Button == binding.Code:
                pressed = mouse.Pressed;
                break;
            case InputEventGamepadButton pad when binding.Kind == InputBindingKind.GamepadButton
                                                  && (int)pad.Button == binding.Code && binding.MatchesDevice(pad.Device):
                pressed = pad.Pressed;
                break;
            case InputEventGamepadAxis axis when binding.Kind == InputBindingKind.GamepadAxis && binding.MatchesDevice(axis.Device)
                                                 && InputState.AxisValue(axis, (GamepadAxisCode)binding.Code, out var value):
                strength = action.AxisStrength(value, binding.Direction);
                pressed = strength > 0f;
                return true;
            default:
                return false;
        }

        strength = pressed ? 1f : 0f;
        return true;
    }
}
