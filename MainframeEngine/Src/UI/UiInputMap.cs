using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>Maps engine input (Silk.NET keys, gamepad buttons) to RmlUi key identifiers. Pure; unit-tested.</summary>
public static class UiInputMap
{
    private static readonly RmlKey[] Keys = BuildKeyTable();

    /// <summary>The RmlUi key for a Silk.NET key (<see cref="RmlKey.Unknown"/> when there is none).</summary>
    public static RmlKey ToRmlKey(Key key) => (uint)key < (uint)Keys.Length ? Keys[(int)key] : RmlKey.Unknown;

    /// <summary>The modifier flag a key toggles while held (shift, ctrl, alt, meta), or none.</summary>
    public static RmlKeyModifiers ModifierOf(Key key) => key switch
    {
        Key.ShiftLeft or Key.ShiftRight => RmlKeyModifiers.Shift,
        Key.ControlLeft or Key.ControlRight => RmlKeyModifiers.Ctrl,
        Key.AltLeft or Key.AltRight => RmlKeyModifiers.Alt,
        Key.SuperLeft or Key.SuperRight => RmlKeyModifiers.Meta,
        _ => RmlKeyModifiers.None,
    };

    /// <summary>
    /// Gamepad buttons as navigation keys, so RmlUi's focus navigation (<c>nav-*</c>, tab order) and default actions
    /// work: D-pad → arrows, A → Return (click the focused element), B → Escape, Start → Return.
    /// </summary>
    public static RmlKey ToNavigationKey(ButtonName button) => button switch
    {
        ButtonName.DPadUp => RmlKey.Up,
        ButtonName.DPadDown => RmlKey.Down,
        ButtonName.DPadLeft => RmlKey.Left,
        ButtonName.DPadRight => RmlKey.Right,
        ButtonName.A => RmlKey.Return,
        ButtonName.Start => RmlKey.Return,
        ButtonName.B => RmlKey.Escape,
        _ => RmlKey.Unknown,
    };

    /// <summary>The navigation key for a stick position past <paramref name="threshold"/> (dominant axis; y up is negative in SDL).</summary>
    public static RmlKey StickDirection(float x, float y, float threshold)
    {
        if (MathF.Abs(x) < threshold && MathF.Abs(y) < threshold)
            return RmlKey.Unknown;
        if (MathF.Abs(x) >= MathF.Abs(y))
            return x > 0 ? RmlKey.Right : RmlKey.Left;
        return y > 0 ? RmlKey.Down : RmlKey.Up;
    }

    /// <summary>Silk mouse buttons → RmlUi button indices (left 0, right 1, middle 2, extra buttons 3+).</summary>
    public static int ToRmlButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        MouseButton.Unknown => -1,
        _ => (int)button,
    };

    private static RmlKey[] BuildKeyTable()
    {
        var table = new RmlKey[(int)Key.Menu + 1];
        void Set(Key k, RmlKey r) => table[(int)k] = r;

        Set(Key.Space, RmlKey.Space);
        for (var i = 0; i < 10; i++)
            Set(Key.Number0 + i, RmlKey.D0 + i);
        for (var i = 0; i < 26; i++)
            Set(Key.A + i, RmlKey.A + i);
        Set(Key.Semicolon, RmlKey.Oem1);
        Set(Key.Equal, RmlKey.OemPlus);
        Set(Key.Comma, RmlKey.OemComma);
        Set(Key.Minus, RmlKey.OemMinus);
        Set(Key.Period, RmlKey.OemPeriod);
        Set(Key.Slash, RmlKey.Oem2);
        Set(Key.GraveAccent, RmlKey.Oem3);
        Set(Key.LeftBracket, RmlKey.Oem4);
        Set(Key.BackSlash, RmlKey.Oem5);
        Set(Key.RightBracket, RmlKey.Oem6);
        Set(Key.Apostrophe, RmlKey.Oem7);
        for (var i = 0; i < 10; i++)
            Set(Key.Keypad0 + i, RmlKey.Numpad0 + i);
        Set(Key.KeypadEnter, RmlKey.NumpadEnter);
        Set(Key.KeypadMultiply, RmlKey.Multiply);
        Set(Key.KeypadAdd, RmlKey.Add);
        Set(Key.KeypadSubtract, RmlKey.Subtract);
        Set(Key.KeypadDecimal, RmlKey.Decimal);
        Set(Key.KeypadDivide, RmlKey.Divide);
        Set(Key.KeypadEqual, RmlKey.OemNecEqual);
        Set(Key.Backspace, RmlKey.Back);
        Set(Key.Tab, RmlKey.Tab);
        Set(Key.Enter, RmlKey.Return);
        Set(Key.Pause, RmlKey.Pause);
        Set(Key.CapsLock, RmlKey.Capital);
        Set(Key.Escape, RmlKey.Escape);
        Set(Key.PageUp, RmlKey.Prior);
        Set(Key.PageDown, RmlKey.Next);
        Set(Key.End, RmlKey.End);
        Set(Key.Home, RmlKey.Home);
        Set(Key.Left, RmlKey.Left);
        Set(Key.Up, RmlKey.Up);
        Set(Key.Right, RmlKey.Right);
        Set(Key.Down, RmlKey.Down);
        Set(Key.PrintScreen, RmlKey.Snapshot);
        Set(Key.Insert, RmlKey.Insert);
        Set(Key.Delete, RmlKey.Delete);
        Set(Key.SuperLeft, RmlKey.LeftMeta);
        Set(Key.SuperRight, RmlKey.RightMeta);
        Set(Key.Menu, RmlKey.Apps);
        for (var i = 0; i < 24; i++)
            Set(Key.F1 + i, RmlKey.F1 + i);
        Set(Key.NumLock, RmlKey.NumLock);
        Set(Key.ScrollLock, RmlKey.Scroll);
        Set(Key.ShiftLeft, RmlKey.LeftShift);
        Set(Key.ShiftRight, RmlKey.RightShift);
        Set(Key.ControlLeft, RmlKey.LeftControl);
        Set(Key.ControlRight, RmlKey.RightControl);
        Set(Key.AltLeft, RmlKey.LeftMenu);
        Set(Key.AltRight, RmlKey.RightMenu);
        return table;
    }
}
