using System.Globalization;
using MainframeEngine.Localization;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>
/// The display-name table behind <see cref="InputBinding.DisplayName"/>. Every name is a literal passed to
/// <see cref="Tr.P(string, string)"/> with the context <c>input</c>, so the extractor finds them; letters, digits,
/// function keys and the gamepad's printed labels (A, LB, RT, LS) are not translated.
/// </summary>
internal static class InputBindingNames
{
    public static string Get(InputBinding binding)
    {
        var name = binding.Kind switch
        {
            InputBindingKind.Key => KeyName((Key)binding.Code),
            InputBindingKind.MouseButton => MouseName((MouseButton)binding.Code),
            InputBindingKind.GamepadButton => PadButtonName((ButtonName)binding.Code),
            InputBindingKind.GamepadAxis => AxisName((GamepadAxisCode)binding.Code, binding.Direction),
            _ => "",
        };
        return binding.Device >= 0 && binding.DeviceClass == InputDeviceClass.Gamepad
            ? Tr.P("input", "{0} (Pad {1})", name, binding.Device + 1)
            : name;
    }

    private static string KeyName(Key key)
    {
        if (key is >= Key.A and <= Key.Z)
            return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.Number0 and <= Key.Number9)
            return ((char)('0' + (key - Key.Number0))).ToString();
        if (key is >= Key.F1 and <= Key.F25)
            return "F" + (key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture);
        if (key is >= Key.Keypad0 and <= Key.Keypad9)
            return Tr.P("input", "Numpad {0}", key - Key.Keypad0);
        var mac = OperatingSystem.IsMacOS();
        return key switch
        {
            Key.Space => Tr.P("input", "Space"),
            Key.Apostrophe => "'",
            Key.Comma => ",",
            Key.Minus => "-",
            Key.Period => ".",
            Key.Slash => "/",
            Key.Semicolon => ";",
            Key.Equal => "=",
            Key.LeftBracket => "[",
            Key.BackSlash => "\\",
            Key.RightBracket => "]",
            Key.GraveAccent => "`",
            Key.Escape => Tr.P("input", "Esc"),
            Key.Enter => Tr.P("input", "Enter"),
            Key.Tab => Tr.P("input", "Tab"),
            Key.Backspace => Tr.P("input", "Backspace"),
            Key.Insert => Tr.P("input", "Insert"),
            Key.Delete => Tr.P("input", "Delete"),
            Key.Right => Tr.P("input", "Right Arrow"),
            Key.Left => Tr.P("input", "Left Arrow"),
            Key.Down => Tr.P("input", "Down Arrow"),
            Key.Up => Tr.P("input", "Up Arrow"),
            Key.PageUp => Tr.P("input", "Page Up"),
            Key.PageDown => Tr.P("input", "Page Down"),
            Key.Home => Tr.P("input", "Home"),
            Key.End => Tr.P("input", "End"),
            Key.CapsLock => Tr.P("input", "Caps Lock"),
            Key.ScrollLock => Tr.P("input", "Scroll Lock"),
            Key.NumLock => Tr.P("input", "Num Lock"),
            Key.PrintScreen => Tr.P("input", "Print Screen"),
            Key.Pause => Tr.P("input", "Pause"),
            Key.KeypadDecimal => Tr.P("input", "Numpad ."),
            Key.KeypadDivide => Tr.P("input", "Numpad /"),
            Key.KeypadMultiply => Tr.P("input", "Numpad *"),
            Key.KeypadSubtract => Tr.P("input", "Numpad -"),
            Key.KeypadAdd => Tr.P("input", "Numpad +"),
            Key.KeypadEnter => Tr.P("input", "Numpad Enter"),
            Key.KeypadEqual => Tr.P("input", "Numpad ="),
            Key.ShiftLeft => Tr.P("input", "Left Shift"),
            Key.ShiftRight => Tr.P("input", "Right Shift"),
            Key.ControlLeft => Tr.P("input", "Left Ctrl"),
            Key.ControlRight => Tr.P("input", "Right Ctrl"),
            Key.AltLeft => mac ? Tr.P("input", "Left Option") : Tr.P("input", "Left Alt"),
            Key.AltRight => mac ? Tr.P("input", "Right Option") : Tr.P("input", "Right Alt"),
            Key.SuperLeft => mac ? Tr.P("input", "Left Cmd") : OperatingSystem.IsWindows() ? Tr.P("input", "Left Win") : Tr.P("input", "Left Super"),
            Key.SuperRight => mac ? Tr.P("input", "Right Cmd") : OperatingSystem.IsWindows() ? Tr.P("input", "Right Win") : Tr.P("input", "Right Super"),
            Key.Menu => Tr.P("input", "Menu"),
            _ => key.ToString(),
        };
    }

    private static string MouseName(MouseButton button) => button switch
    {
        MouseButton.Left => Tr.P("input", "Mouse Left"),
        MouseButton.Right => Tr.P("input", "Mouse Right"),
        MouseButton.Middle => Tr.P("input", "Mouse Middle"),
        >= MouseButton.Button4 => Tr.P("input", "Mouse {0}", button - MouseButton.Button4 + 4),
        _ => button.ToString(),
    };

    // The Xbox layout: the printed labels stay as they are; the rest is translated.
    private static string PadButtonName(ButtonName button) => button switch
    {
        ButtonName.A => "A",
        ButtonName.B => "B",
        ButtonName.X => "X",
        ButtonName.Y => "Y",
        ButtonName.LeftBumper => "LB",
        ButtonName.RightBumper => "RB",
        ButtonName.LeftStick => "LS",
        ButtonName.RightStick => "RS",
        ButtonName.Back => Tr.P("input", "Back"),
        ButtonName.Start => Tr.P("input", "Start"),
        ButtonName.Home => Tr.P("input", "Guide"),
        ButtonName.DPadUp => Tr.P("input", "D-Pad Up"),
        ButtonName.DPadDown => Tr.P("input", "D-Pad Down"),
        ButtonName.DPadLeft => Tr.P("input", "D-Pad Left"),
        ButtonName.DPadRight => Tr.P("input", "D-Pad Right"),
        _ => button.ToString(),
    };

    // Stick Y is positive down (SDL), so LeftY- is Tr.P("input", "LS Up").
    private static string AxisName(GamepadAxisCode axis, int direction) => (axis, direction < 0) switch
    {
        (GamepadAxisCode.LeftX, true) => Tr.P("input", "LS Left"),
        (GamepadAxisCode.LeftX, false) => Tr.P("input", "LS Right"),
        (GamepadAxisCode.LeftY, true) => Tr.P("input", "LS Up"),
        (GamepadAxisCode.LeftY, false) => Tr.P("input", "LS Down"),
        (GamepadAxisCode.RightX, true) => Tr.P("input", "RS Left"),
        (GamepadAxisCode.RightX, false) => Tr.P("input", "RS Right"),
        (GamepadAxisCode.RightY, true) => Tr.P("input", "RS Up"),
        (GamepadAxisCode.RightY, false) => Tr.P("input", "RS Down"),
        (GamepadAxisCode.LeftTrigger, false) => "LT",
        (GamepadAxisCode.RightTrigger, false) => "RT",
        (GamepadAxisCode.LeftTrigger, true) => "LT −",
        (GamepadAxisCode.RightTrigger, true) => "RT −",
        _ => axis.ToString(),
    };
}
