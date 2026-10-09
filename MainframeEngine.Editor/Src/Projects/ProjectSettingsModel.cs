using System.Globalization;
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>The kind of editor a project setting gets in the Project Settings dialog.</summary>
public enum SettingKind
{
    Text,
    WholeNumber,
    Number,
    Bool,
    Choice,

    /// <summary>A comma-separated list of names.</summary>
    List,

    /// <summary>"x, y" / "x, y, z".</summary>
    Vector,

    /// <summary>A scene file (picker of the project's <c>.mscene</c> files).</summary>
    Scene,

    /// <summary>A content file (picker; <see cref="ProjectSetting.Filter"/>).</summary>
    File,

    ReadOnly,
}

/// <summary>One editable value of <c>project.mfproj</c>: its key (<c>window.width</c>), section, label, editor and accessors.</summary>
public sealed record ProjectSetting(
    string Key,
    string Section,
    string Label,
    SettingKind Kind,
    Func<ProjectSettings, string> Get,
    Action<ProjectSettings, string>? Set,
    string Tooltip = "",
    IReadOnlyList<string>? Choices = null,
    string? Filter = null);

/// <summary>
/// The Project Settings dialog's model: a working copy of the project's <see cref="ProjectSettings"/>, every editable
/// setting by key (<see cref="Settings"/> — all sections of <c>project.mfproj</c>), the input map and autoload
/// operations, an undo history and save. Each edit is recorded as a before/after snapshot of the whole file
/// (<see cref="ProjectSettings.ToJson"/>), so undo restores exactly what was there; edits that change nothing are not
/// recorded. Invalid values are rejected with a message naming the setting.
/// </summary>
public sealed class ProjectSettingsModel
{
    private readonly string _filePath;

    public ProjectSettingsModel(ProjectSettings settings, string filePath)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        Current = Clone(settings);
    }

    /// <summary>The working copy (replaced by undo/redo: read it again after every change).</summary>
    public ProjectSettings Current { get; private set; }

    public UndoRedo History { get; } = new();

    /// <summary>Unsaved edits.</summary>
    public bool IsDirty => History.IsDirty;

    public string FilePath => _filePath;

    /// <summary>Raised after every change of <see cref="Current"/> (edit, undo, redo).</summary>
    public event Action? Changed;

    /// <summary>The dialog's sections, in order (name, Tabler icon).</summary>
    public static readonly IReadOnlyList<(string Name, string Icon)> Sections =
    [
        ("Application", "app-window"),
        ("Window", "browser"),
        ("Input Map", "keyboard"),
        ("Physics", "ball-bowling"),
        ("Audio", "volume"),
        ("Localization", "language"),
        ("Rendering", "brightness-half"),
        ("Autoloads", "stack-2"),
        ("Steamworks", "brand-steam"),
    ];

    /// <summary>Every scalar setting (input map and autoloads have their own operations).</summary>
    public static readonly IReadOnlyList<ProjectSetting> Settings =
    [
        new("name", "Application", "Name", SettingKind.Text, static s => s.Name, static (s, v) => s.Name = v.Trim(),
            "The game's name: the default window title and its user-data folder."),
        new("mainScene", "Application", "Main Scene", SettingKind.Scene, static s => s.MainScene ?? "",
            static (s, v) => s.MainScene = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), "The scene the game starts with (Play ▶ runs it)."),
        new("assemblies", "Application", "Game Assemblies", SettingKind.List, static s => string.Join(", ", s.Assemblies),
            static (s, v) => Replace(s.Assemblies, SplitList(v)), "Assemblies holding the game's node types (loaded before scenes)."),
        new("isDemo", "Application", "Demo Build", SettingKind.Bool, static s => Bool(s.IsDemo), static (s, v) => s.IsDemo = ParseBool(v),
            "This build is the game's Steam demo: game code compiles with DEMO defined (#if DEMO) and Steam starts with the Demo App ID. " +
            "A build can override it: -p:MainframeDemo=true|false."),
        new("engineVersion", "Application", "Engine Version", SettingKind.ReadOnly, static s => s.EngineVersion, null,
            "The engine version the project was created with."),

        new("window.title", "Window", "Title", SettingKind.Text, static s => s.Window.Title ?? "",
            static (s, v) => s.Window.Title = string.IsNullOrWhiteSpace(v) ? null : v, "Empty: the project name."),
        new("window.width", "Window", "Width", SettingKind.WholeNumber, static s => Int(s.Window.Width), static (s, v) => s.Window.Width = ParseInt(v)),
        new("window.height", "Window", "Height", SettingKind.WholeNumber, static s => Int(s.Window.Height), static (s, v) => s.Window.Height = ParseInt(v)),
        new("window.vsync", "Window", "VSync", SettingKind.Bool, static s => Bool(s.Window.VSync), static (s, v) => s.Window.VSync = ParseBool(v)),
        new("window.maxFps", "Window", "Max FPS", SettingKind.WholeNumber, static s => Int(s.Window.MaxFps), static (s, v) => s.Window.MaxFps = ParseInt(v),
            "0: no frame cap."),
        new("window.icon", "Window", "Icon", SettingKind.File, static s => s.Window.Icon ?? "",
            static (s, v) => s.Window.Icon = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), "A PNG for the window and taskbar.", Filter: "*.png"),

        new("physics.ticksPerSecond", "Physics", "Ticks Per Second", SettingKind.WholeNumber, static s => Int(s.Physics.TicksPerSecond),
            static (s, v) => s.Physics.TicksPerSecond = ParseInt(v), "The fixed physics tick rate."),
        new("physics.maxStepsPerFrame", "Physics", "Max Steps Per Frame", SettingKind.WholeNumber, static s => Int(s.Physics.MaxStepsPerFrame),
            static (s, v) => s.Physics.MaxStepsPerFrame = ParseInt(v), "Catch-up limit after a slow frame."),
        new("physics.3d.gravity", "Physics", "3D Gravity", SettingKind.Vector, static s => Vec(s.Physics.Physics3D.Gravity),
            static (s, v) => s.Physics.Physics3D.Gravity = ParseVector3(v)),
        new("physics.3d.substeps", "Physics", "3D Substeps", SettingKind.WholeNumber, static s => Int(s.Physics.Physics3D.SubstepCount),
            static (s, v) => s.Physics.Physics3D.SubstepCount = ParseInt(v)),
        new("physics.3d.solverIterations", "Physics", "3D Solver Iterations", SettingKind.WholeNumber, static s => Int(s.Physics.Physics3D.SolverIterations),
            static (s, v) => s.Physics.Physics3D.SolverIterations = ParseInt(v)),
        new("physics.3d.relaxationIterations", "Physics", "3D Relaxation Iterations", SettingKind.WholeNumber,
            static s => Int(s.Physics.Physics3D.RelaxationIterations), static (s, v) => s.Physics.Physics3D.RelaxationIterations = ParseInt(v)),
        new("physics.3d.allowDeactivation", "Physics", "3D Sleeping", SettingKind.Bool, static s => Bool(s.Physics.Physics3D.AllowDeactivation),
            static (s, v) => s.Physics.Physics3D.AllowDeactivation = ParseBool(v)),
        new("physics.3d.multiThreaded", "Physics", "3D Multi-threaded", SettingKind.Bool, static s => Bool(s.Physics.Physics3D.MultiThreaded),
            static (s, v) => s.Physics.Physics3D.MultiThreaded = ParseBool(v)),
        new("physics.3d.deterministic", "Physics", "3D Deterministic", SettingKind.Bool, static s => Bool(s.Physics.Physics3D.Deterministic),
            static (s, v) => s.Physics.Physics3D.Deterministic = ParseBool(v), "Same results on every run (single-threaded, slower)."),
        new("physics.2d.gravity", "Physics", "2D Gravity", SettingKind.Vector, static s => Vec(s.Physics.Physics2D.Gravity),
            static (s, v) => s.Physics.Physics2D.Gravity = ParseVector2(v), "Pixels per second²."),
        new("physics.2d.pixelsPerMeter", "Physics", "2D Pixels Per Meter", SettingKind.Number, static s => Num(s.Physics.Physics2D.PixelsPerMeter),
            static (s, v) => s.Physics.Physics2D.PixelsPerMeter = ParseFloat(v)),
        new("physics.2d.substeps", "Physics", "2D Substeps", SettingKind.WholeNumber, static s => Int(s.Physics.Physics2D.SubstepCount),
            static (s, v) => s.Physics.Physics2D.SubstepCount = ParseInt(v)),
        new("physics.2d.allowSleep", "Physics", "2D Sleeping", SettingKind.Bool, static s => Bool(s.Physics.Physics2D.AllowSleep),
            static (s, v) => s.Physics.Physics2D.AllowSleep = ParseBool(v)),
        new("physics.2d.continuous", "Physics", "2D Continuous", SettingKind.Bool, static s => Bool(s.Physics.Physics2D.EnableContinuous),
            static (s, v) => s.Physics.Physics2D.EnableContinuous = ParseBool(v), "Continuous collision for fast bodies."),

        new("audio.enabled", "Audio", "Enabled", SettingKind.Bool, static s => Bool(s.Audio.Enabled), static (s, v) => s.Audio.Enabled = ParseBool(v)),
        new("audio.busLayout", "Audio", "Bus Layout", SettingKind.File, static s => s.Audio.BusLayout ?? "",
            static (s, v) => s.Audio.BusLayout = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            "The AudioBusLayout resource (.mres) the mixer starts with.", Filter: "*.mres"),
        new("audio.sampleRate", "Audio", "Sample Rate", SettingKind.WholeNumber, static s => Int(s.Audio.SampleRate),
            static (s, v) => s.Audio.SampleRate = ParseInt(v)),
        new("audio.bufferMs", "Audio", "Buffer (ms)", SettingKind.WholeNumber, static s => Int(s.Audio.BufferMilliseconds),
            static (s, v) => s.Audio.BufferMilliseconds = ParseInt(v)),

        new("localization.defaultLocale", "Localization", "Default Locale", SettingKind.Text, static s => s.Localization.DefaultLocale ?? "",
            static (s, v) => s.Localization.DefaultLocale = string.IsNullOrWhiteSpace(v) ? null : v.Trim(), "Empty: the player's OS language."),
        new("localization.sourceLocale", "Localization", "Source Locale", SettingKind.Text, static s => s.Localization.SourceLocale,
            static (s, v) => s.Localization.SourceLocale = Required(v, "the source locale")),
        new("localization.fallbacks", "Localization", "Fallbacks", SettingKind.List, static s => string.Join(", ", s.Localization.Fallbacks),
            static (s, v) => Replace(s.Localization.Fallbacks, SplitList(v))),
        new("localization.directory", "Localization", "Catalog Folder", SettingKind.Text, static s => s.Localization.LocaleDirectory,
            static (s, v) => s.Localization.LocaleDirectory = Required(v, "the catalog folder")),
        new("localization.domain", "Localization", "Domain", SettingKind.Text, static s => s.Localization.Domain,
            static (s, v) => s.Localization.Domain = Required(v, "the domain")),

        new("rendering.exposure", "Rendering", "Exposure", SettingKind.Number, static s => Num(s.Rendering.Exposure),
            static (s, v) => s.Rendering.Exposure = ParseFloat(v)),
        new("rendering.shadows", "Rendering", "Shadow Quality", SettingKind.Choice, static s => s.Rendering.Shadows.ToString(),
            static (s, v) => s.Rendering.Shadows = Enum.Parse<ShadowQuality>(v, ignoreCase: true),
            "Off, or the Low/Medium/High atlas size, filter and cascades.", Choices: Enum.GetNames<ShadowQuality>()),
        new("rendering.antiAliasing", "Rendering", "Anti-Aliasing", SettingKind.Choice, static s => s.Rendering.AntiAliasing.ToString(),
            static (s, v) => s.Rendering.AntiAliasing = Enum.Parse<AntiAliasing>(v, ignoreCase: true),
            "None, FXAA on the tonemapped image, or TAA (temporal: smooths leaves and thin detail). The UI stays sharp.",
            Choices: Enum.GetNames<AntiAliasing>()),
        new("rendering.taaSharpness", "Rendering", "TAA Sharpness", SettingKind.Number, static s => Num(s.Rendering.TaaSharpness),
            static (s, v) => s.Rendering.TaaSharpness = ParseFloat(v), "0 (off) to 1: sharpens the image after TAA."),
        new("rendering.waterSsr", "Rendering", "Water Reflections", SettingKind.Choice, static s => s.Rendering.WaterSsr.ToString(),
            static (s, v) => s.Rendering.WaterSsr = Enum.Parse<WaterSsrQuality>(v, ignoreCase: true),
            "Screen-space reflections on refracting water: Off (sky only), Low or High.", Choices: Enum.GetNames<WaterSsrQuality>()),
        new("rendering.scaling3DMode", "Rendering", "3D Scaling Mode", SettingKind.Choice, static s => s.Rendering.Scaling3DMode.ToString(),
            static (s, v) => s.Rendering.Scaling3DMode = Enum.Parse<Scaling3DMode>(v, ignoreCase: true),
            "How the 3D view upscales below a scale of 1: Bilinear, FSR 1, or TAAU (temporal; turns TAA on). The UI stays at full resolution.",
            Choices: Enum.GetNames<Scaling3DMode>()),
        new("rendering.scaling3DScale", "Rendering", "3D Scale", SettingKind.Number, static s => Num(s.Rendering.Scaling3DScale),
            static (s, v) => s.Rendering.Scaling3DScale = ParseFloat(v), "0.25 to 1: the 3D view's render resolution (0.75 renders 1440p as 1080p)."),
        new("rendering.fsrSharpness", "Rendering", "FSR Sharpness", SettingKind.Number, static s => Num(s.Rendering.FsrSharpness),
            static (s, v) => s.Rendering.FsrSharpness = ParseFloat(v), "0 (sharpest) to 2: the sharpen after an FSR 1 upscale."),

        new("steam.appId", "Steamworks", "App ID", SettingKind.WholeNumber, static s => s.Steam.AppId.ToString(CultureInfo.InvariantCulture),
            static (s, v) => s.Steam.AppId = ParseUInt(v), "The full game's Steam app id (480: Spacewar, Valve's test app). 0 leaves Steam alone."),
        new("steam.demoAppId", "Steamworks", "Demo App ID", SettingKind.WholeNumber, static s => s.Steam.DemoAppId.ToString(CultureInfo.InvariantCulture),
            static (s, v) => s.Steam.DemoAppId = ParseUInt(v), "The demo's own Steam app id, used by demo builds (Application › Demo Build). 0 leaves Steam alone in the demo."),
        new("steam.devAppIdFile", "Steamworks", "Run Outside Steam in Development", SettingKind.Bool, static s => Bool(s.Steam.DevAppIdFile),
            static (s, v) => s.Steam.DevAppIdFile = ParseBool(v),
            "Play and Debug builds write steam_appid.txt next to the game, so Steam starts without launching the game through Steam. Never in Release builds."),
        new("steam.restartThroughSteam", "Steamworks", "Relaunch Through Steam", SettingKind.Bool, static s => Bool(s.Steam.RestartThroughSteam),
            static (s, v) => s.Steam.RestartThroughSteam = ParseBool(v),
            "Release builds started outside Steam ask Steam to start them again and exit, so ownership checks and the overlay always apply."),
    ];

    /// <summary>The setting with <paramref name="key"/>, or null.</summary>
    public static ProjectSetting? Find(string key) => Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    /// <summary>The text of setting <paramref name="key"/> in <see cref="Current"/>.</summary>
    public string Get(string key) => (Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key))).Get(Current);

    /// <summary>Sets <paramref name="key"/> from <paramref name="text"/> (undoable); returns null, or why the value was rejected.</summary>
    public string? Set(string key, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var setting = Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.", nameof(key));
        if (setting.Set is not { } set)
            return $"{setting.Label} is read-only.";
        return Edit($"Set {setting.Label}", s => set(s, text));
    }

    // ── Input map ────────────────────────────────────────────────────────────────────────────────────────────────

    public string? AddAction(string name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "Name the action.";
        if (Current.Input.HasAction(trimmed))
            return $"There is already an action named '{trimmed}'.";
        return Edit($"Add Action {trimmed}", s => s.Input.AddAction(trimmed));
    }

    public string? RemoveAction(string name) => Edit($"Remove Action {name}", s =>
    {
        if (!s.Input.RemoveAction(name))
            throw new ArgumentException($"There is no action named '{name}'.");
    });

    public string? RenameAction(string name, string newName)
    {
        var trimmed = newName?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "Name the action.";
        if (string.Equals(name, trimmed, StringComparison.Ordinal))
            return null;
        if (Current.Input.HasAction(trimmed))
            return $"There is already an action named '{trimmed}'.";
        return Edit($"Rename Action {name}", s =>
        {
            // Rebuild the map in order with the new name (InputMap keeps insertion order).
            var copy = new InputMap();
            foreach (var action in s.Input.Actions)
            {
                var added = copy.AddAction(string.Equals(action.Name, name, StringComparison.Ordinal) ? trimmed : action.Name, action.Deadzone);
                added.Bindings.AddRange(action.Bindings);
            }

            s.Input = copy;
        });
    }

    public string? SetDeadzone(string action, string text) => Edit($"Set {action} Deadzone", s =>
    {
        var target = s.Input.GetAction(action) ?? throw new ArgumentException($"There is no action named '{action}'.");
        target.Deadzone = ParseFloat(text);
    });

    /// <summary>Adds <paramref name="binding"/> (a captured key/mouse button, or a gamepad input from the list) to <paramref name="action"/>.</summary>
    public string? AddBinding(string action, InputBinding binding)
    {
        if (Current.Input.GetAction(action) is not { } target)
            return $"There is no action named '{action}'.";
        if (target.Bindings.Contains(binding))
            return $"'{binding}' is already bound to {action}.";
        return Edit($"Bind {binding} to {action}", s => s.Input.Bind(action, binding));
    }

    public string? RemoveBinding(string action, InputBinding binding) => Edit($"Unbind {binding} from {action}", s =>
    {
        if (!s.Input.Unbind(action, binding))
            throw new ArgumentException($"'{binding}' is not bound to {action}.");
    });

    /// <summary>The binding for a key pressed while capturing (Escape cancels, so it is never captured).</summary>
    public static InputBinding? Capture(Key key) => key is Key.Unknown or Key.Escape ? null : InputBinding.Key(key);

    public static InputBinding? Capture(MouseButton button) => button == MouseButton.Unknown ? null : InputBinding.Mouse(button);

    /// <summary>Every gamepad button and axis direction (any pad), for the "add gamepad binding" list.</summary>
    public static IReadOnlyList<InputBinding> GamepadBindings()
    {
        var list = new List<InputBinding>();
        foreach (var button in Enum.GetValues<ButtonName>())
            if (button != ButtonName.Unknown)
                list.Add(InputBinding.GamepadButton(button));
        foreach (var axis in Enum.GetValues<GamepadAxisCode>())
        {
            list.Add(InputBinding.GamepadAxis(axis, -1));
            list.Add(InputBinding.GamepadAxis(axis, 1));
        }

        return list;
    }

    // ── Autoloads ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Adds an autoload running <paramref name="sceneOrType"/> (a <c>.mscene</c> path or a node type name).</summary>
    public string? AddAutoload(string name, string sceneOrType)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || string.IsNullOrWhiteSpace(sceneOrType))
            return "An autoload needs a name and a scene or node type.";
        if (Current.Autoloads.Any(a => string.Equals(a.Name, trimmed, StringComparison.Ordinal)))
            return $"There is already an autoload named '{trimmed}'.";
        var isScene = sceneOrType.EndsWith(".mscene", StringComparison.OrdinalIgnoreCase) || AssetUid.IsUid(sceneOrType);
        return Edit($"Add Autoload {trimmed}", s => s.Autoloads.Add(new AutoloadSettings
        {
            Name = trimmed,
            Scene = isScene ? sceneOrType.Trim() : null,
            Type = isScene ? null : sceneOrType.Trim(),
        }));
    }

    public string? RemoveAutoload(int index) => Edit("Remove Autoload", s =>
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)s.Autoloads.Count, nameof(index));
        s.Autoloads.RemoveAt(index);
    });

    public string? MoveAutoload(int index, int delta) => Edit("Move Autoload", s =>
    {
        var target = index + delta;
        if ((uint)index >= (uint)s.Autoloads.Count || (uint)target >= (uint)s.Autoloads.Count)
            throw new ArgumentOutOfRangeException(nameof(delta));
        (s.Autoloads[index], s.Autoloads[target]) = (s.Autoloads[target], s.Autoloads[index]);
    });

    public string? SetAutoloadEnabled(int index, bool enabled) => Edit("Toggle Autoload", s => s.Autoloads[index].Enabled = enabled);

    // ── Edits, undo, save ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Applies <paramref name="change"/> to a copy of <see cref="Current"/>; the copy becomes current when it differs.</summary>
    public string? Edit(string name, Action<ProjectSettings> change)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(change);
        var before = Current.ToJson();
        var working = Clone(Current);
        try
        {
            change(working);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or InvalidOperationException or OverflowException)
        {
            return e.Message;
        }

        var after = working.ToJson();
        if (before.AsSpan().SequenceEqual(after))
            return null;
        History.Commit(new SnapshotAction(this, name, before, after));
        return null;
    }

    /// <summary>Writes <c>project.mfproj</c>; false (with the reason logged) when it failed.</summary>
    public bool Save()
    {
        try
        {
            Current.Save(_filePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[Editor] Could not save {_filePath}: {e.Message}");
            return false;
        }

        History.MarkSaved();
        Changed?.Invoke();
        return true;
    }

    private void Apply(byte[] json)
    {
        Current = ProjectSettings.Parse(json, _filePath);
        Changed?.Invoke();
    }

    private static ProjectSettings Clone(ProjectSettings settings) => ProjectSettings.Parse(settings.ToJson(), ProjectSettings.FileName);

    private sealed class SnapshotAction(ProjectSettingsModel model, string name, byte[] before, byte[] after) : IEditorAction
    {
        public string Name => name;

        public void Do() => model.Apply(after);

        public void Undo() => model.Apply(before);
    }

    // ── Parsing helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Num(float value) => ValueText.Number(value);
    private static string Bool(bool value) => value ? "true" : "false";
    private static string Vec(Vector2 v) => $"{ValueText.Number(v.X)}, {ValueText.Number(v.Y)}";
    private static string Vec(Vector3 v) => $"{ValueText.Number(v.X)}, {ValueText.Number(v.Y)}, {ValueText.Number(v.Z)}";

    private static int ParseInt(string text) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a whole number.");

    private static uint ParseUInt(string text) =>
        uint.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"'{text}' is not a positive whole number.");

    private static float ParseFloat(string text) =>
        ValueText.TryParseFloat(text, out var value) && float.IsFinite(value) ? value : throw new FormatException($"'{text}' is not a number.");

    private static bool ParseBool(string text) =>
        bool.TryParse(text.Trim(), out var value) ? value : throw new FormatException($"'{text}' is not true or false.");

    private static Vector2 ParseVector2(string text)
    {
        var parts = SplitNumbers(text, 2);
        return new Vector2(parts[0], parts[1]);
    }

    private static Vector3 ParseVector3(string text)
    {
        var parts = SplitNumbers(text, 3);
        return new Vector3(parts[0], parts[1], parts[2]);
    }

    private static float[] SplitNumbers(string text, int count)
    {
        var parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != count)
            throw new FormatException($"Enter {count} numbers separated by commas.");
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = ParseFloat(parts[i]);
        return values;
    }

    private static string Required(string text, string what) =>
        string.IsNullOrWhiteSpace(text) ? throw new FormatException($"Enter {what}.") : text.Trim();

    private static List<string> SplitList(string text) =>
        [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static void Replace(List<string> list, List<string> values)
    {
        list.Clear();
        list.AddRange(values);
    }
}
