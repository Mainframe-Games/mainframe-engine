using System.Globalization;
using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>The tabs a <see cref="SettingsMenu"/> shows.</summary>
[Flags]
public enum SettingsTabs
{
    Audio = 1,
    Controls = 2,
    Display = 4,
    All = Audio | Controls | Display,
}

/// <summary>How <see cref="SettingsMenu.Open"/> builds the menu.</summary>
public sealed class SettingsMenuOptions
{
    /// <summary>Pause the tree while the menu is open (the menu's layer keeps processing); restored on close.</summary>
    public bool PauseTree { get; init; }

    /// <summary>The tabs to show, in the order Audio, Controls, Display.</summary>
    public SettingsTabs Tabs { get; init; } = SettingsTabs.All;

    /// <summary>Buses without a row (by name; e.g. a "Voice" bus the game drives itself). Master is always shown.</summary>
    public IReadOnlyCollection<string> HiddenBuses { get; init; } = [];

    /// <summary>Display text per bus name (translated through the game's catalog: <c>["SFX"] = "Effects"</c>).</summary>
    public Dictionary<string, string> BusLabels { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The document (a restyled copy of <see cref="SettingsMenu.DefaultSource"/> that keeps its data model).</summary>
    public string Source { get; init; } = SettingsMenu.DefaultSource;

    /// <summary>The menu layer's order (90: above game menus, under the developer overlay).</summary>
    public int Layer { get; init; } = 90;

    /// <summary>The settings the menu edits; null uses <see cref="UserSettings.Current"/>.</summary>
    public UserSettings? Settings { get; init; }
}

/// <summary>
/// The engine's drop-in settings menu: audio volumes, key rebinding and display options over <see cref="UserSettings"/>,
/// laid out by <c>Content/UI/settings/settings.rml</c> with the widget library. <see cref="Open"/> adds it on its own
/// <see cref="UiLayer"/>; every change applies at once and the settings are saved when the menu closes (only if
/// something changed). LB/RB switch tabs; Escape or gamepad B closes it. Strings go through <see cref="Tr"/> (context
/// <c>"settings"</c>), so a game's catalog translates them (merge <c>Content/UI/settings/settings.pot</c>).
/// </summary>
public sealed class SettingsMenu : UiDocument
{
    /// <summary>The engine's menu document.</summary>
    public const string DefaultSource = "Content/UI/settings/settings.rml";

    /// <summary>The name of the layer <see cref="Open"/> creates (one menu per tree).</summary>
    public const string LayerName = "SettingsMenu";

    /// <summary>Seconds before a display change the player does not confirm is reverted.</summary>
    public const float RevertSeconds = 15f;

    private sealed class BusRow
    {
        public required SettingsMenu Menu { get; init; }
        public required string Name { get; init; }
        public string Label { get; set; } = "";
    }

    private sealed class ActionRow
    {
        public required SettingsMenu Menu { get; init; }
        public required int Index { get; init; }
        public required string Name { get; init; }
        public string Label { get; set; } = "";
        public string Key1 { get; set; } = "";
        public string Key2 { get; set; } = "";
        public string Pad { get; set; } = "";
        public bool Locked { get; set; }
    }

    private sealed class Choice
    {
        public required string Value { get; init; }
        public string Label { get; set; } = "";
    }

    private static readonly RmlStructType<BusRow> BusType = new RmlStructType<BusRow>()
        .Member("name", static b => b.Name)
        .Member("label", static b => b.Label)
        .Member("volume", static b => b.Menu.Settings.GetBusVolume(b.Name), static (b, v) => b.Menu.SetVolume(b, v))
        .Member("percent", static b => (int)MathF.Round(b.Menu.Settings.GetBusVolume(b.Name) * 100f))
        .Member("muted", static b => b.Menu.Settings.IsBusMuted(b.Name));

    private static readonly RmlStructType<ActionRow> ActionType = new RmlStructType<ActionRow>()
        .Member("label", static a => a.Label)
        .Member("key1", static a => a.Key1)
        .Member("key2", static a => a.Key2)
        .Member("pad", static a => a.Pad)
        .Member("capturing", static a => a.Menu._captureAction == a.Index ? a.Menu._captureSlot : -1)
        .Member("locked", static a => a.Locked);

    private static readonly RmlStructType<Choice> ChoiceType = new RmlStructType<Choice>()
        .Member("value", static c => c.Value)
        .Member("label", static c => c.Label);

    private static readonly RmlStructType<Choice> LocaleType = new RmlStructType<Choice>()
        .Member("code", static c => c.Value)
        .Member("name", static c => c.Label);

    private static readonly (int Width, int Height)[] CommonSizes =
    [
        (1280, 720), (1280, 800), (1366, 768), (1440, 900), (1600, 900), (1680, 1050), (1920, 1080), (1920, 1200),
        (2560, 1080), (2560, 1440), (2560, 1600), (3440, 1440), (3840, 2160),
    ];

    private static readonly int[] FpsLimits = [0, 30, 60, 90, 120, 144, 165, 240];

    private readonly List<BusRow> _buses = [];
    private readonly List<ActionRow> _actions = [];
    private readonly List<string> _windowSizes = [];
    private readonly List<Choice> _fpsChoices = [];
    private readonly List<Choice> _locales = [];
    private RmlDataModel? _model;
    private InputRebinder? _rebinder;
    private UserSettings? _settings;
    private SettingsMenuOptions _options = new();
    private string _tab = "audio";
    private string _notice = "";
    private int _captureAction = -1;
    private int _captureSlot = -1; // 0, 1: keyboard/mouse slots; 2: the gamepad slot
    private bool _changed;
    private bool _wasPaused;
    private bool _closing;

    // Display revert prompt: the mode and size before an unconfirmed change.
    private bool _revertPending;
    private float _revertLeft;
    private WindowMode _revertMode;
    private Vector2I? _revertSize;
    private string _revertText = "";

    public SettingsMenu()
    {
        Source = DefaultSource;
        Modal = true;
        InputBeforeUi = true; // LB/RB and Escape before RmlUi takes them
    }

    /// <summary>Raised once when the menu closes (after the settings were saved).</summary>
    [Signal]
    public event Action? Closed;

    /// <summary>The settings this menu edits.</summary>
    public UserSettings Settings => _settings ?? throw new InvalidOperationException("The settings menu has no UserSettings (none loaded: UserSettings.Current is null).");

    /// <summary>The open tab: <c>"audio"</c>, <c>"controls"</c> or <c>"display"</c>.</summary>
    public string Tab => _tab;

    /// <summary>Whether a binding is waiting for a key or button.</summary>
    public bool IsCapturing => _captureAction >= 0;

    /// <summary>Whether the display revert countdown is running.</summary>
    public bool RevertPending => _revertPending;

    /// <summary>
    /// Opens the menu in <paramref name="tree"/> (on a <see cref="UiLayer"/> named <see cref="LayerName"/> below the root),
    /// or returns the one already open.
    /// </summary>
    public static SettingsMenu Open(SceneTree tree, SettingsMenuOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (Find(tree) is { } open)
            return open;
        options ??= new SettingsMenuOptions();
        var layer = new UiLayer { Name = LayerName, Layer = options.Layer, ProcessMode = ProcessMode.Always };
        var menu = new SettingsMenu { Name = "Settings", _options = options, Source = options.Source };
        layer.AddChild(menu);
        tree.Root.AddChild(layer);
        return menu;
    }

    /// <summary>The open menu of <paramref name="tree"/>, or null.</summary>
    public static SettingsMenu? Find(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return tree.Root.GetNodeOrNull(LayerName) is UiLayer { IsQueuedForDeletion: false } layer && layer.GetNodeOrNull("Settings") is SettingsMenu menu ? menu : null;
    }

    /// <summary>Saves (if anything changed), restores the pause state and frees the menu's layer.</summary>
    public void Close()
    {
        if (_closing)
            return;
        _closing = true;
        if (_revertPending)
            Revert();
        _rebinder?.Cancel();
        if (_changed && _settings?.FilePath is not null)
        {
            try
            {
                _settings.Save();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"[Settings] Could not save the settings: {e.Message}");
            }
        }

        if (_options.PauseTree && Tree is { } tree)
            tree.Paused = _wasPaused;
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        Closed?.Invoke();
        if (Layer is { Name: LayerName } layer)
            layer.QueueFree();
        else
            QueueFree();
    }

    /// <summary>Switches to <paramref name="tab"/> (<c>"audio"</c>, <c>"controls"</c>, <c>"display"</c>) if it is shown.</summary>
    public void ShowTab(string tab)
    {
        if (!IsShown(tab) || tab == _tab)
            return;
        CancelCapture();
        _tab = tab;
        _model?.Dirty("tab");
    }

    protected override void OnReady()
    {
        _settings = _options.Settings ?? UserSettings.Current;
        if (_settings is null)
        {
            Log.Warning("[Settings] No UserSettings loaded: the settings menu edits in-memory defaults and saves nothing.");
            _settings = new UserSettings(new ConfigFile(), GameHost.Project);
        }
        if (_options.PauseTree && Tree is { } tree)
        {
            _wasPaused = tree.Paused;
            tree.Paused = true;
        }

        _tab = (_options.Tabs & SettingsTabs.Audio) != 0 ? "audio" : (_options.Tabs & SettingsTabs.Controls) != 0 ? "controls" : "display";
        _rebinder = new InputRebinder { Name = "Rebinder" };
        _rebinder.Captured += OnCaptured;
        _rebinder.Cleared += OnCleared;
        _rebinder.Cancelled += CancelCapture;
        AddChild(_rebinder);
        if (_settings is not null)
            _settings.Changed += OnSettingsChanged;

        BuildBuses();
        BuildActions();
        BuildDisplayChoices();
        Translate();

        _model = CreateDataModel("settings")
            .Bind("tab", this, static m => m._tab)
            .Bind("showAudio", this, static m => (m._options.Tabs & SettingsTabs.Audio) != 0)
            .Bind("showControls", this, static m => (m._options.Tabs & SettingsTabs.Controls) != 0)
            .Bind("showDisplay", this, static m => (m._options.Tabs & SettingsTabs.Display) != 0)
            .BindList("buses", _buses, BusType)
            .BindList("actions", _actions, ActionType)
            .Bind("notice", this, static m => m._notice)
            .Bind("showWindow", this, static m => m.Tree?.Window is not null && !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS())
            .Bind("windowMode", this, static m => m.ModeName, static (m, v) => m.SetWindowMode(v))
            .BindList("windowSizes", _windowSizes)
            .Bind("windowSize", this, static m => m.SizeName, static (m, v) => m.SetWindowSize(v))
            .Bind("vsync", this, static m => m.Settings.VSync, static (m, v) => m.Settings.VSync = v)
            .BindList("maxFpsOptions", _fpsChoices, ChoiceType)
            .Bind("maxFps", this, static m => m.Settings.MaxFps.ToString(CultureInfo.InvariantCulture), static (m, v) => m.SetMaxFps(v))
            .Bind("uiScale", this, static m => m.Settings.UiScale, static (m, v) => m.Settings.UiScale = v)
            .Bind("uiScalePercent", this, static m => (int)MathF.Round(m.Settings.UiScale * 100f))
            .Bind("shadows", this, static m => m.Settings.Shadows.ToString(), static (m, v) => m.SetShadows(v))
            .Bind("shadowsRestart", this, static m => m.Settings.ShadowsNeedRestart)
            .BindList("locales", _locales, LocaleType)
            .Bind("language", this, static m => m.Settings.Language ?? Tr.CurrentLocale, static (m, v) => m.SetLanguage(v))
            .Bind("revertPending", this, static m => m._revertPending)
            .Bind("revertText", this, static m => m._revertText)
            .Event("tab", e => ShowTab(e.GetArgument(0).GetString()))
            .Event("toggleMute", e => ToggleMute((int)e.GetArgument(0).GetDouble()))
            .Event("rebind", e => BeginRebind((int)e.GetArgument(0).GetDouble(), (int)e.GetArgument(1).GetDouble(), (int)e.GetArgument(2).GetDouble()))
            .Event("reset", e => Reset(e.GetArgument(0).GetString()))
            .Event("keep", KeepDisplay)
            .Event("revert", Revert)
            .Event("close", Close);
    }

    protected override void OnExitTree()
    {
        if (_settings is not null)
            _settings.Changed -= OnSettingsChanged;
        if (_options.PauseTree && !_closing && Tree is { } tree)
            tree.Paused = _wasPaused;
        base.OnExitTree();
    }

    protected override void OnLocaleChanged()
    {
        Translate();
        _model?.DirtyAll();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_revertPending)
            return;
        var before = (int)MathF.Ceiling(_revertLeft);
        _revertLeft -= gameTime.DeltaTime;
        if (_revertLeft <= 0f)
        {
            Revert();
            return;
        }

        var seconds = (int)MathF.Ceiling(_revertLeft);
        if (seconds != before)
        {
            _revertText = Tr.N("Reverting in {0} second", "Reverting in {0} seconds", seconds);
            _model?.Dirty("revertText");
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        if (IsCapturing)
            return; // the rebinder (a child, so it saw the event first) owns input while capturing
        switch (inputEvent)
        {
            case InputEventGamepadButton { Pressed: true, Button: ButtonName.LeftBumper }:
                StepTab(-1);
                GetViewport()?.SetInputAsHandled();
                break;
            case InputEventGamepadButton { Pressed: true, Button: ButtonName.RightBumper }:
                StepTab(1);
                GetViewport()?.SetInputAsHandled();
                break;
            case InputEventKey { Pressed: true, Key: Key.Escape }:
            case InputEventGamepadButton { Pressed: true, Button: ButtonName.B }:
                if (_revertPending)
                    Revert();
                else
                    Close();
                GetViewport()?.SetInputAsHandled();
                break;
        }
    }

    // ── Audio ────────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildBuses()
    {
        _buses.Clear();
        if (Tree?.Servers.Get<AudioServer>() is not { } audio)
            return;
        foreach (var bus in audio.Buses)
        {
            if (!ReferenceEquals(bus, audio.Master) && _options.HiddenBuses.Contains(bus.Name))
                continue;
            _buses.Add(new BusRow { Menu = this, Name = bus.Name });
        }
    }

    private void SetVolume(BusRow row, float volume)
    {
        if (_settings is null || MathF.Abs(_settings.GetBusVolume(row.Name) - volume) < 1e-4f)
            return;
        _settings.SetBusVolume(row.Name, volume);
        _model?.Dirty("buses");
    }

    private void ToggleMute(int index)
    {
        if (_settings is null || (uint)index >= (uint)_buses.Count)
            return;
        var name = _buses[index].Name;
        _settings.SetBusMuted(name, !_settings.IsBusMuted(name));
        _model?.Dirty("buses");
    }

    // ── Controls ─────────────────────────────────────────────────────────────────────────────────────────────

    private InputMap? Map => Tree?.Input.Map;

    private void BuildActions()
    {
        _actions.Clear();
        if (Map is not { } map)
            return;
        foreach (var action in map.Actions)
            _actions.Add(new ActionRow { Menu = this, Index = _actions.Count, Name = action.Name, Locked = !action.Rebindable });
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        if (Map is not { } map)
            return;
        foreach (var row in _actions)
        {
            row.Key1 = BindingText(map, row.Name, InputDeviceClass.KeyboardMouse, 0);
            row.Key2 = BindingText(map, row.Name, InputDeviceClass.KeyboardMouse, 1);
            row.Pad = BindingText(map, row.Name, InputDeviceClass.Gamepad, 0);
        }

        _model?.Dirty("actions");
    }

    private static string BindingText(InputMap map, string action, InputDeviceClass deviceClass, int slot) =>
        map.GetBinding(action, deviceClass, slot) is { Kind: not InputBindingKind.None } binding ? binding.DisplayName() : "—";

    private void BeginRebind(int action, int deviceClass, int slot)
    {
        if ((uint)action >= (uint)_actions.Count || _rebinder is null)
            return;
        var row = _actions[action];
        if (row.Locked)
        {
            _notice = Tr.P("settings", "{0} cannot be changed.", row.Label);
            _model?.Dirty("notice");
            return;
        }

        _captureAction = action;
        _captureSlot = deviceClass == 1 ? 2 : Math.Clamp(slot, 0, 1);
        _notice = deviceClass == 1
            ? Tr.P("settings", "Press a gamepad button. Back cancels.")
            : Tr.P("settings", "Press a key or mouse button. Esc cancels, Backspace clears.");
        _rebinder.Begin(deviceClass == 1 ? InputDeviceClass.Gamepad : InputDeviceClass.KeyboardMouse);
        _model?.Dirty("actions");
        _model?.Dirty("notice");
    }

    private void OnCaptured(InputBinding binding)
    {
        if (_captureAction < 0 || Map is not { } map)
            return;
        var row = _actions[_captureAction];
        var (deviceClass, slot) = CaptureTarget();
        var result = map.Rebind(row.Name, deviceClass, slot, binding);
        _notice = result switch
        {
            { Applied: false, ConflictWith: { } locked } => Tr.P("settings", "{0} is used by {1}.", binding.DisplayName(), LabelOf(locked)),
            { Applied: true, ConflictWith: { } other } => Tr.P("settings", "{0} was swapped with {1}.", binding.DisplayName(), LabelOf(other)),
            _ => "",
        };
        EndCapture(changed: result.Applied);
    }

    private void OnCleared()
    {
        if (_captureAction < 0 || Map is not { } map)
            return;
        var (deviceClass, slot) = CaptureTarget();
        map.Rebind(_actions[_captureAction].Name, deviceClass, slot, default); // an empty binding clears the slot
        _notice = "";
        EndCapture(changed: true);
    }

    private (InputDeviceClass DeviceClass, int Slot) CaptureTarget() =>
        _captureSlot == 2 ? (InputDeviceClass.Gamepad, 0) : (InputDeviceClass.KeyboardMouse, _captureSlot);

    private void EndCapture(bool changed)
    {
        _captureAction = -1;
        _captureSlot = -1;
        if (changed)
        {
            Settings.StoreInput(Map!);
            _changed = true;
        }

        RefreshBindings();
        _model?.Dirty("notice");
    }

    private void CancelCapture()
    {
        if (_captureAction < 0)
            return;
        _rebinder?.Cancel();
        _captureAction = -1;
        _captureSlot = -1;
        _notice = "";
        _model?.Dirty("actions");
        _model?.Dirty("notice");
    }

    private string LabelOf(InputAction action) => action.Label is { } label ? Atr(label) : action.Name;

    // ── Display ──────────────────────────────────────────────────────────────────────────────────────────────

    private string ModeName => Settings.WindowMode switch
    {
        WindowMode.Maximized => "Maximized",
        WindowMode.Fullscreen or WindowMode.ExclusiveFullscreen => "Fullscreen",
        _ => "Windowed",
    };

    private string SizeName => Settings.WindowSize is { } size
        ? SizeValue(size.X, size.Y)
        : Tree?.Window is { } window ? SizeValue((int)window.Size.X, (int)window.Size.Y) : "";

    private static string SizeValue(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width} × {height}");

    private void BuildDisplayChoices()
    {
        _windowSizes.Clear();
        var screen = Tree?.Window?.ScreenBounds ?? default;
        foreach (var (w, h) in CommonSizes)
        {
            if (screen.Size.X > 0 && (w > screen.Size.X || h > screen.Size.Y))
                continue;
            _windowSizes.Add(SizeValue(w, h));
        }

        if (SizeName is { Length: > 0 } current && !_windowSizes.Contains(current))
            _windowSizes.Add(current);
        _fpsChoices.Clear();
        foreach (var fps in FpsLimits)
            _fpsChoices.Add(new Choice { Value = fps.ToString(CultureInfo.InvariantCulture) });
        _locales.Clear();
        foreach (var code in Tr.GetAvailableLocales())
            _locales.Add(new Choice { Value = code, Label = NativeName(code) });

        // A select writes its first option back when the bound value is not one of its options: keep the current
        // language in the list, so opening the menu never changes it.
        var language = _settings?.Language ?? Tr.CurrentLocale;
        if (!_locales.Exists(l => l.Value == language))
            _locales.Insert(0, new Choice { Value = language, Label = NativeName(language) });
    }

    private static string NativeName(string code)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(code.Replace('_', '-'));
            if (culture.NativeName is { Length: > 0 } native && !string.Equals(native, code, StringComparison.OrdinalIgnoreCase))
                return char.ToUpper(native[0], culture) + native[1..];
        }
        catch (CultureNotFoundException)
        {
        }

        return code;
    }

    private void SetWindowMode(string value)
    {
        if (Tree?.Window is null)
            return; // no window rows (and no write-back from the hidden select)
        var mode = value switch
        {
            "Maximized" => WindowMode.Maximized,
            "Fullscreen" => WindowMode.Fullscreen,
            _ => WindowMode.Windowed,
        };
        if (_settings is null || mode == _settings.WindowMode)
            return;
        BeginRevert();
        _settings.WindowMode = mode;
        _model?.Dirty("windowSize");
    }

    private void SetWindowSize(string value)
    {
        if (Tree?.Window is null || _settings is null || value == SizeName)
            return;
        var parts = value.Split('×', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var w) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var h))
            return;
        BeginRevert();
        _settings.WindowSize = new Vector2I(w, h);
    }

    private void SetMaxFps(string value)
    {
        if (_settings is not null && int.TryParse(value, CultureInfo.InvariantCulture, out var fps) && fps != _settings.MaxFps)
            _settings.MaxFps = fps;
    }

    private void SetShadows(string value)
    {
        if (_settings is not null && Enum.TryParse<ShadowQuality>(value, out var quality) && quality != _settings.Shadows)
        {
            _settings.Shadows = quality;
            _model?.Dirty("shadowsRestart");
        }
    }

    private void SetLanguage(string value)
    {
        if (_settings is not null && !string.IsNullOrEmpty(value) && value != (_settings.Language ?? Tr.CurrentLocale))
            _settings.Language = value; // reloads the document at the next UI frame (the server defers it)
    }

    private void BeginRevert()
    {
        if (!_revertPending)
        {
            _revertMode = Settings.WindowMode;
            _revertSize = Settings.WindowSize;
        }

        _revertPending = true;
        _revertLeft = RevertSeconds;
        _revertText = Tr.N("Reverting in {0} second", "Reverting in {0} seconds", (int)RevertSeconds);
        _model?.Dirty("revertPending");
        _model?.Dirty("revertText");
    }

    private void KeepDisplay()
    {
        _revertPending = false;
        _model?.Dirty("revertPending");
    }

    private void Revert()
    {
        if (!_revertPending)
            return;
        _revertPending = false;
        Settings.WindowMode = _revertMode;
        Settings.WindowSize = _revertSize;
        _model?.Dirty("revertPending");
        _model?.Dirty("windowMode");
        _model?.Dirty("windowSize");
    }

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────────────────

    private void Reset(string section)
    {
        if (_settings is null)
            return;
        switch (section)
        {
            case "audio":
                _settings.Reset(UserSettingsSection.Audio);
                break;
            case "controls":
                CancelCapture();
                _settings.Reset(UserSettingsSection.Input); // also resets the tree's map
                _notice = "";
                RefreshBindings();
                break;
            case "display":
                if (_revertPending)
                    KeepDisplay();
                _settings.Reset(UserSettingsSection.Display);
                break;
            default:
                return;
        }

        _changed = true;
        _model?.DirtyAll();
    }

    private void OnSettingsChanged(UserSettingsSection section)
    {
        _changed = true;
        switch (section)
        {
            case UserSettingsSection.Audio:
                _model?.Dirty("buses");
                break;
            case UserSettingsSection.Display:
                _model?.Dirty("windowMode");
                _model?.Dirty("windowSize");
                _model?.Dirty("vsync");
                _model?.Dirty("maxFps");
                _model?.Dirty("uiScale");
                _model?.Dirty("uiScalePercent");
                _model?.Dirty("shadows");
                _model?.Dirty("shadowsRestart");
                break;
            case UserSettingsSection.Language:
                _model?.Dirty("language");
                break;
        }
    }

    private bool IsShown(string tab) => tab switch
    {
        "audio" => (_options.Tabs & SettingsTabs.Audio) != 0,
        "controls" => (_options.Tabs & SettingsTabs.Controls) != 0,
        "display" => (_options.Tabs & SettingsTabs.Display) != 0,
        _ => false,
    };

    private void StepTab(int step)
    {
        string[] order = ["audio", "controls", "display"];
        var at = Array.IndexOf(order, _tab);
        for (var i = 1; i <= order.Length; i++)
        {
            var next = order[(at + step * i + order.Length * 3) % order.Length];
            if (IsShown(next))
            {
                ShowTab(next);
                return;
            }
        }
    }

    private void Translate()
    {
        foreach (var bus in _buses)
            bus.Label = _options.BusLabels.TryGetValue(bus.Name, out var label) ? Atr(label) : DefaultBusLabel(bus.Name);
        if (Map is { } map)
        {
            foreach (var row in _actions)
                row.Label = map.GetAction(row.Name) is { Label: { } label } ? Atr(label) : row.Name;
        }

        foreach (var choice in _fpsChoices)
            choice.Label = choice.Value == "0" ? Tr.P("settings", "Unlimited") : choice.Value;
    }

    // Literals at the call site so the extractor sees them.
    private static string DefaultBusLabel(string name) => name switch
    {
        "Master" => Tr.P("settings", "Master"),
        "Music" => Tr.P("settings", "Music"),
        "SFX" => Tr.P("settings", "Effects"),
        "UI" => Tr.P("settings", "Interface"),
        "Voice" => Tr.P("settings", "Voice"),
        "Ambience" => Tr.P("settings", "Ambience"),
        _ => name,
    };
}
