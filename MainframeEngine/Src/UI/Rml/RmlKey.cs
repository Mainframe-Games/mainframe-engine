namespace MainframeEngine.UI.Rml;

/// <summary>RmlUi key identifiers (<c>Rml::Input::KeyIdentifier</c>, RmlUi 6.3; values asserted in the native shim).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Mirrors Rml::Input::KeyIdentifier (KI_DECIMAL).")]
public enum RmlKey
{
    Unknown = 0,
    Space = 1,
    D0 = 2, D1 = 3, D2 = 4, D3 = 5, D4 = 6, D5 = 7, D6 = 8, D7 = 9, D8 = 10, D9 = 11,
    A = 12, B = 13, C = 14, D = 15, E = 16, F = 17, G = 18, H = 19, I = 20, J = 21, K = 22, L = 23, M = 24,
    N = 25, O = 26, P = 27, Q = 28, R = 29, S = 30, T = 31, U = 32, V = 33, W = 34, X = 35, Y = 36, Z = 37,

    /// <summary>US layout: semicolon.</summary>
    Oem1 = 38,
    OemPlus = 39,
    OemComma = 40,
    OemMinus = 41,
    OemPeriod = 42,

    /// <summary>US layout: slash.</summary>
    Oem2 = 43,

    /// <summary>US layout: backtick.</summary>
    Oem3 = 44,

    /// <summary>US layout: left bracket.</summary>
    Oem4 = 45,

    /// <summary>US layout: backslash.</summary>
    Oem5 = 46,

    /// <summary>US layout: right bracket.</summary>
    Oem6 = 47,

    /// <summary>US layout: quote.</summary>
    Oem7 = 48,
    Oem8 = 49,
    Oem102 = 50,
    Numpad0 = 51, Numpad1 = 52, Numpad2 = 53, Numpad3 = 54, Numpad4 = 55,
    Numpad5 = 56, Numpad6 = 57, Numpad7 = 58, Numpad8 = 59, Numpad9 = 60,
    NumpadEnter = 61,
    Multiply = 62,
    Add = 63,
    Separator = 64,
    Subtract = 65,
    Decimal = 66,
    Divide = 67,
    OemNecEqual = 68,
    Back = 69,
    Tab = 70,
    Clear = 71,
    Return = 72,
    Pause = 73,
    Capital = 74,
    Kana = 75,
    Hangul = 76,
    Junja = 77,
    Final = 78,
    Hanja = 79,
    Kanji = 80,
    Escape = 81,
    Convert = 82,
    NonConvert = 83,
    Accept = 84,
    ModeChange = 85,

    /// <summary>Page up.</summary>
    Prior = 86,

    /// <summary>Page down.</summary>
    Next = 87,
    End = 88,
    Home = 89,
    Left = 90,
    Up = 91,
    Right = 92,
    Down = 93,
    Select = 94,
    Print = 95,
    Execute = 96,
    Snapshot = 97,
    Insert = 98,
    Delete = 99,
    Help = 100,
    LeftWin = 101,
    RightWin = 102,
    Apps = 103,
    Power = 104,
    Sleep = 105,
    Wake = 106,
    F1 = 107, F2 = 108, F3 = 109, F4 = 110, F5 = 111, F6 = 112, F7 = 113, F8 = 114, F9 = 115, F10 = 116,
    F11 = 117, F12 = 118, F13 = 119, F14 = 120, F15 = 121, F16 = 122, F17 = 123, F18 = 124, F19 = 125, F20 = 126,
    F21 = 127, F22 = 128, F23 = 129, F24 = 130,
    NumLock = 131,
    Scroll = 132,
    OemFjJisho = 133,
    OemFjMasshou = 134,
    OemFjTouroku = 135,
    OemFjLoya = 136,
    OemFjRoya = 137,
    LeftShift = 138,
    RightShift = 139,
    LeftControl = 140,
    RightControl = 141,

    /// <summary>Left Alt.</summary>
    LeftMenu = 142,

    /// <summary>Right Alt.</summary>
    RightMenu = 143,
    BrowserBack = 144,
    BrowserForward = 145,
    BrowserRefresh = 146,
    BrowserStop = 147,
    BrowserSearch = 148,
    BrowserFavorites = 149,
    BrowserHome = 150,
    VolumeMute = 151,
    VolumeDown = 152,
    VolumeUp = 153,
    MediaNextTrack = 154,
    MediaPrevTrack = 155,
    MediaStop = 156,
    MediaPlayPause = 157,
    LaunchMail = 158,
    LaunchMediaSelect = 159,
    LaunchApp1 = 160,
    LaunchApp2 = 161,
    OemAx = 162,
    IcoHelp = 163,
    Ico00 = 164,
    ProcessKey = 165,
    IcoClear = 166,
    Attn = 167,
    CrSel = 168,
    ExSel = 169,
    ErEof = 170,
    Play = 171,
    Zoom = 172,
    Pa1 = 173,
    OemClear = 174,
    LeftMeta = 175,
    RightMeta = 176,
}

/// <summary>Key modifier flags (<c>Rml::Input::KeyModifier</c>).</summary>
[Flags]
public enum RmlKeyModifiers
{
    None = 0,
    Ctrl = 1 << 0,
    Shift = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3,
    CapsLock = 1 << 4,
    NumLock = 1 << 5,
    ScrollLock = 1 << 6,
}

/// <summary>Mouse buttons as RmlUi numbers them.</summary>
public enum RmlMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
}
