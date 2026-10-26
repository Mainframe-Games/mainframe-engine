using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using MainframeEngine.Localization;
using Silk.NET.Maths;

namespace MainframeEngine;

/// <summary>A part of <see cref="UserSettings"/>: what <see cref="UserSettings.Changed"/> reports and <see cref="UserSettings.Reset"/> clears.</summary>
public enum UserSettingsSection
{
    /// <summary>Bus volumes and mutes (<c>audio</c>).</summary>
    Audio,

    /// <summary>Rebound actions (<c>input</c>).</summary>
    Input,

    /// <summary>Window mode and size, VSync, frame cap, UI scale and shadows (<c>display</c>).</summary>
    Display,

    /// <summary>The player's language (<c>locale</c>).</summary>
    Language,

    /// <summary>The game's own sections of <see cref="UserSettings.Config"/>.</summary>
    Game,
}

/// <summary>
/// The player's options, in <c>{user data}/settings.json</c>: the typed audio, input, display and language settings the
/// engine applies itself, over a <see cref="ConfigFile"/> (<see cref="Config"/>) whose other sections belong to the game.
/// <see cref="GameHost"/> loads it right after <c>project.mfproj</c> (<see cref="Current"/>), patches the engine options
/// before the window opens (<see cref="PatchEngineOptions"/>: VSync, windowed size, locale) and applies the rest in
/// <see cref="GameSession.Start"/> before the autoloads (<see cref="Apply"/>). From then on every setter applies at once
/// and raises <see cref="Changed"/>; saving is explicit (<see cref="Save"/>).
/// </summary>
/// <remarks>
/// <para>
/// Every key is optional: a missing key means the project's default, so the file never pins a value the player did not
/// change. Setting a value equal to the project's default erases its key (the window size, which has no "default" value
/// of its own, is erased by setting null). <c>input</c> holds the full binding list (text form, <see cref="InputBinding"/>)
/// of only the actions whose bindings differ from <c>project.mfproj</c>; actions and buses that no longer exist are
/// dropped with a warning.
/// </para>
/// <para>
/// Before <see cref="Apply"/> attaches a tree, setters only store values. Values that cannot change live: a shadow switch to
/// or from <see cref="ShadowQuality.Off"/> once visuals exist is saved and applies at the next start
/// (<see cref="ShadowsNeedRestart"/>). Game-thread only.
/// </para>
/// </remarks>
public sealed class UserSettings
{
    /// <summary>The settings file's name in the user data folder.</summary>
    public const string FileName = "settings.json";

    /// <summary>Smallest <see cref="UiScale"/>.</summary>
    public const float MinUiScale = 0.75f;

    /// <summary>Largest <see cref="UiScale"/>.</summary>
    public const float MaxUiScale = 2f;

    private const string AudioSection = "audio";
    private const string InputSection = "input";
    private const string DisplaySection = "display";
    private const string LocaleSection = "locale";

    private readonly ProjectSettings _project;
    private readonly List<string> _strings = [];
    private SceneTree? _tree;

    /// <summary>
    /// Settings over <paramref name="config"/>, with <paramref name="project"/>'s values as the defaults (null: a new
    /// project's) and <paramref name="path"/> as where <see cref="Save"/> writes (null: in memory only).
    /// <see cref="DefaultInput"/> is cloned from the project's map now, before any override is applied.
    /// </summary>
    public UserSettings(ConfigFile config, ProjectSettings? project = null, string? path = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _project = project ?? new ProjectSettings();
        DefaultInput = _project.Input.Clone();
        FilePath = string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path);
    }

    /// <summary>The running game's settings: set by <see cref="GameHost.Run"/> (and <see cref="HeadlessHost"/>, for <see cref="Config"/> only), like <see cref="GameHost.Project"/>.</summary>
    public static UserSettings? Current { get; internal set; }

    /// <summary>The file: the engine's sections (<c>audio</c>, <c>input</c>, <c>display</c>, <c>locale</c>) and the game's own.</summary>
    public ConfigFile Config { get; }

    /// <summary>A clone of <c>project.mfproj</c>'s input map, taken before the player's overrides: what "reset to defaults" restores.</summary>
    public InputMap DefaultInput { get; }

    /// <summary>Where <see cref="Save"/> writes (null for in-memory settings).</summary>
    public string? FilePath { get; }

    /// <summary>The tree <see cref="Apply"/> attached (live changes go to its window and servers), or null.</summary>
    public SceneTree? Tree => _tree;

    /// <summary>Raised after a setter, <see cref="StoreInput"/> or <see cref="Reset"/> changed something.</summary>
    public event Action<UserSettingsSection>? Changed;

    /// <summary>The settings file of <paramref name="project"/>: <see cref="FileName"/> in <see cref="UserDataPaths.GetDirectory"/> of its <see cref="ProjectSettings.UserDataFolderName"/>.</summary>
    public static string GetPath(ProjectSettings project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Path.Combine(UserDataPaths.GetDirectory(project.UserDataFolderName), FileName);
    }

    /// <summary>Loads <paramref name="project"/>'s settings file (<see cref="GetPath"/>). Never throws for bad content (<see cref="ConfigFile.Load"/>).</summary>
    public static UserSettings Load(ProjectSettings project) => Load(GetPath(project), project);

    /// <summary>Loads the settings at <paramref name="path"/> (missing: all defaults). Never throws for bad content (<see cref="ConfigFile.Load"/>).</summary>
    public static UserSettings Load(string path, ProjectSettings? project = null) => new(ConfigFile.Load(path), project, path);

    // ── Audio ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The player's volume of <paramref name="bus"/> (<see cref="AudioBus.UserVolume"/>), 0..1; 1 when unset.</summary>
    public float GetBusVolume(string bus)
    {
        ArgumentException.ThrowIfNullOrEmpty(bus);
        if (Volumes(create: false)?[bus] is not { } node)
            return 1f;
        if (ConfigFile.TryGetNumber(node, out var volume))
            return Math.Clamp((float)volume, 0f, 1f);
        Config.WarnOnce("audio.volume." + bus, $"audio.volume.{bus}", "a number");
        return 1f;
    }

    /// <summary>Sets the player's volume of <paramref name="bus"/> (clamped to 0..1, NaN = 1; 1 erases the key) and applies it.</summary>
    public void SetBusVolume(string bus, float volume)
    {
        ArgumentException.ThrowIfNullOrEmpty(bus);
        volume = float.IsNaN(volume) ? 1f : (float)Math.Round(Math.Clamp(volume, 0f, 1f), 4);
        var changed = GetBusVolume(bus) != volume;
        if (volume >= 1f)
        {
            if (Volumes(create: false) is { } volumes)
            {
                volumes.Remove(bus);
                if (volumes.Count == 0)
                    Config.EraseSectionKey(AudioSection, "volume");
            }
        }
        else
        {
            Volumes(create: true)![bus] = Math.Round((double)volume, 4); // 0.7, not 0.699999988
        }

        if (Bus(bus) is { } live)
            live.UserVolume = volume;
        if (changed)
            Changed?.Invoke(UserSettingsSection.Audio);
    }

    /// <summary>Whether the player muted <paramref name="bus"/>.</summary>
    public bool IsBusMuted(string bus)
    {
        ArgumentException.ThrowIfNullOrEmpty(bus);
        Config.GetStrings(AudioSection, "mute", _strings);
        return _strings.Contains(bus);
    }

    /// <summary>Mutes or unmutes <paramref name="bus"/> (<see cref="AudioBus.Mute"/>) and applies it.</summary>
    public void SetBusMuted(string bus, bool muted)
    {
        ArgumentException.ThrowIfNullOrEmpty(bus);
        Config.GetStrings(AudioSection, "mute", _strings);
        var changed = _strings.Contains(bus) != muted;
        if (changed)
        {
            if (muted)
                _strings.Add(bus);
            else
                _strings.Remove(bus);
            if (_strings.Count == 0)
                Config.EraseSectionKey(AudioSection, "mute");
            else
                Config.SetValue(AudioSection, "mute", _strings);
        }

        if (Bus(bus) is { } live)
            live.Mute = muted;
        if (changed)
            Changed?.Invoke(UserSettingsSection.Audio);
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Whether the player rebound <paramref name="action"/> (the file holds its binding list).</summary>
    public bool HasInputOverride(string action) => Config.HasSectionKey(InputSection, action);

    /// <summary>
    /// Records <paramref name="map"/>'s bindings (the live map, after a rebind): the full binding list of every action of
    /// <see cref="DefaultInput"/> whose bindings differ (order included), and nothing for the others. Actions that are not
    /// in the project are ignored. Raises <see cref="Changed"/> when the stored overrides change.
    /// </summary>
    public void StoreInput(InputMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var changed = false;
        foreach (var defaults in DefaultInput.Actions)
        {
            if (map.GetAction(defaults.Name) is not { } current)
                continue;
            if (!map.DiffersFrom(defaults.Name, DefaultInput))
            {
                changed |= Config.EraseSectionKey(InputSection, defaults.Name);
                continue;
            }

            var texts = new List<string>(current.Bindings.Count);
            foreach (var binding in current.Bindings)
                texts.Add(binding.ToString());
            if (Config.GetStrings(InputSection, defaults.Name, _strings) && _strings.SequenceEqual(texts))
                continue;
            Config.SetValue(InputSection, defaults.Name, texts);
            changed = true;
        }

        if (changed)
            Changed?.Invoke(UserSettingsSection.Input);
    }

    /// <summary>
    /// Applies the stored overrides to <paramref name="map"/> (each listed action gets exactly the stored bindings). Actions
    /// missing from <see cref="DefaultInput"/> or <paramref name="map"/>, and lists that are not string arrays, are dropped
    /// from the file with a warning; bindings that do not parse are skipped with a warning.
    /// </summary>
    public void ApplyInput(InputMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var bindings = new List<InputBinding>();
        foreach (var action in Config.GetSectionKeys(InputSection))
        {
            if (!DefaultInput.HasAction(action) || !map.HasAction(action))
            {
                Log.Warning($"[Settings] '{Config.Source}': input action '{action}' is not in the project; dropped.");
                Config.EraseSectionKey(InputSection, action);
                continue;
            }

            if (!Config.GetStrings(InputSection, action, _strings))
            {
                Config.EraseSectionKey(InputSection, action); // GetStrings warned
                continue;
            }

            bindings.Clear();
            foreach (var text in _strings)
            {
                if (InputBinding.TryParse(text, out var binding))
                    bindings.Add(binding);
                else
                    Log.Warning($"[Settings] '{Config.Source}': '{text}' (input.{action}) is not a binding; skipped.");
            }

            map.SetBindings(action, CollectionsMarshal.AsSpan(bindings));
        }
    }

    // ── Display ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The window mode (<see cref="IWindowControl.Mode"/>); the project's <see cref="WindowSettings.Mode"/> when unset.</summary>
    public WindowMode WindowMode
    {
        get => EnumValue(DisplaySection, "mode", _project.Window.Mode);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown window mode.");
            if (!SetEnum(DisplaySection, "mode", value, _project.Window.Mode, camelCase: true))
                return;
            if (_tree?.Window is { } window)
                window.Mode = value;
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>
    /// The windowed size in points; null = the project's (<see cref="WindowSettings.Width"/> × <see cref="WindowSettings.Height"/>).
    /// Applied before the window opens and, live, while the window is <see cref="MainframeEngine.WindowMode.Windowed"/>.
    /// </summary>
    public Vector2I? WindowSize
    {
        get
        {
            if (Config.GetNode(DisplaySection, "size") is not { } node)
                return null;
            if (node is JsonArray { Count: 2 } array && array[0] is { } x && array[1] is { } y && ConfigFile.TryGetNumber(x, out var width) &&
                ConfigFile.TryGetNumber(y, out var height) && width is >= 1 and <= 65535 && height is >= 1 and <= 65535)
                return new Vector2I((int)width, (int)height);
            Config.WarnOnce("display.size", "display.size", "[width, height]");
            return null;
        }
        set
        {
            if (value is { } size && (size.X < 1 || size.Y < 1))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The window size must be positive.");
            if (WindowSize == value)
                return;
            if (value is { } set)
                Config.SetNode(DisplaySection, "size", new JsonArray(set.X, set.Y));
            else
                Config.EraseSectionKey(DisplaySection, "size");
            var effective = value ?? new Vector2I(_project.Window.Width, _project.Window.Height);
            if (_tree?.Window is { Mode: WindowMode.Windowed } window)
                window.Size = new System.Numerics.Vector2(effective.X, effective.Y);
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>Vertical sync (<see cref="IWindowControl.VSync"/>); the project's <see cref="WindowSettings.VSync"/> when unset.</summary>
    public bool VSync
    {
        get => Config.GetBool(DisplaySection, "vsync", _project.Window.VSync);
        set
        {
            if (VSync == value)
                return;
            if (value == _project.Window.VSync)
                Config.EraseSectionKey(DisplaySection, "vsync");
            else
                Config.SetValue(DisplaySection, "vsync", value);
            if (_tree?.Window is { } window)
                window.VSync = value;
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>The frame cap while VSync is off (<see cref="IWindowControl.MaxFps"/>, 0 = unlimited); the project's <see cref="WindowSettings.MaxFps"/> when unset.</summary>
    public int MaxFps
    {
        get
        {
            var fps = Config.GetInt(DisplaySection, "maxFps", _project.Window.MaxFps);
            if (fps >= 0)
                return fps;
            Config.WarnOnce("display.maxFps", "display.maxFps", "0 or a positive number");
            return _project.Window.MaxFps;
        }
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (MaxFps == value)
                return;
            if (value == _project.Window.MaxFps)
                Config.EraseSectionKey(DisplaySection, "maxFps");
            else
                Config.SetValue(DisplaySection, "maxFps", value);
            if (_tree?.Window is { } window)
                window.MaxFps = value;
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>The player's UI scale (<see cref="UiServer.UserScale"/>), <see cref="MinUiScale"/>..<see cref="MaxUiScale"/>; 1 when unset.</summary>
    public float UiScale
    {
        get => (float)Math.Clamp(Config.GetDouble(DisplaySection, "uiScale", 1), MinUiScale, MaxUiScale);
        set
        {
            value = float.IsNaN(value) ? 1f : (float)Math.Round(Math.Clamp(value, MinUiScale, MaxUiScale), 3);
            if (UiScale == value)
                return;
            if (value == 1f)
                Config.EraseSectionKey(DisplaySection, "uiScale");
            else
                Config.SetValue(DisplaySection, "uiScale", Math.Round((double)value, 3));
            if (_tree?.Servers.Get<UiServer>() is { } ui)
                ui.UserScale = value;
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>
    /// The shadow budget (<see cref="RenderServer.ShadowQuality"/>); the project's <see cref="RenderingProjectSettings.Shadows"/>
    /// when unset. Low, Medium and High switch live; a switch to or from Off once visuals exist is saved and applies at the
    /// next start (<see cref="ShadowsNeedRestart"/>).
    /// </summary>
    public ShadowQuality Shadows
    {
        get => EnumValue(DisplaySection, "shadows", _project.Rendering.Shadows);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown shadow quality.");
            if (!SetEnum(DisplaySection, "shadows", value, _project.Rendering.Shadows, camelCase: false))
                return;
            if (_tree?.Servers.Render is { } render)
                ApplyShadows(render, value);
            Changed?.Invoke(UserSettingsSection.Display);
        }
    }

    /// <summary>
    /// Whether <see cref="Shadows"/> differs from the running shadow state in a way that cannot change live (to or from
    /// Off once visuals exist): a settings menu shows "applies after restart".
    /// </summary>
    public bool ShadowsNeedRestart =>
        _tree?.Servers.Render is { } render && (render.ShadowQuality == ShadowQuality.Off) != (Shadows == ShadowQuality.Off);

    // ── Language ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The player's language (<c>es</c>, <c>pt_BR</c>; <see cref="Tr.SetLocale"/>); null = the project's default locale,
    /// else the OS language (<see cref="Tr.ResolveStartupLocale"/>).
    /// </summary>
    public string? Language
    {
        get
        {
            var language = Config.GetString(LocaleSection, "language");
            if (language is null || LocaleId.Normalize(language).Length > 0)
                return language;
            Config.WarnOnce("locale.language", "locale.language", "a locale name");
            return null;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                value = null;
            else if (LocaleId.Normalize(value).Length == 0)
                throw new ArgumentException($"Invalid locale name '{value}'.", nameof(value));
            if (Language == value)
                return;
            Config.SetValue(LocaleSection, "language", value);
            if (_tree is not null)
                ApplyLanguage(value);
            Changed?.Invoke(UserSettingsSection.Language);
        }
    }

    // ── Apply, reset, save ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine options the window opens with, patched with the player's values (VSync, windowed size, locale) where
    /// set. <see cref="GameHost"/> calls it on <see cref="ProjectSettings.ToEngineOptions"/>, before command-line flags.
    /// </summary>
    public EngineOptions PatchEngineOptions(EngineOptions options)
    {
        if (Config.HasSectionKey(DisplaySection, "vsync"))
            options.VSync = VSync;
        options.WindowMode = WindowMode;
        if (WindowSize is { } size)
            options.WindowSize = new Vector2D<int>(size.X, size.Y);
        if (Language is { } language)
            options.Locale = language;
        return options;
    }

    /// <summary>
    /// Attaches <paramref name="tree"/> (later changes apply to it live) and applies every value the player set: input
    /// overrides onto the tree's map, bus volumes and mutes, window mode, VSync, frame cap, UI scale, shadows (Off works
    /// here, before visuals exist) and language. Unset values keep what the host applied from the project.
    /// <see cref="GameSession.Start"/> calls it before the autoloads. The window size is applied earlier, through
    /// <see cref="PatchEngineOptions"/>.
    /// </summary>
    public void Apply(SceneTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        ApplyInput(tree.Input.Map);

        if (tree.Servers.Get<AudioServer>() is { } audio)
        {
            if (Volumes(create: false) is { } volumes)
            {
                foreach (var name in Keys(volumes))
                {
                    if (audio.GetBus(name) is { } bus)
                    {
                        bus.UserVolume = GetBusVolume(name);
                    }
                    else
                    {
                        Log.Warning($"[Settings] '{Config.Source}': audio bus '{name}' is not in the layout; dropped.");
                        volumes.Remove(name);
                    }
                }

                if (volumes.Count == 0)
                    Config.EraseSectionKey(AudioSection, "volume");
            }

            if (Config.GetStrings(AudioSection, "mute", _strings))
            {
                var kept = _strings.Count;
                for (var i = _strings.Count - 1; i >= 0; i--)
                {
                    if (audio.GetBus(_strings[i]) is { } bus)
                    {
                        bus.Mute = true;
                    }
                    else
                    {
                        Log.Warning($"[Settings] '{Config.Source}': audio bus '{_strings[i]}' is not in the layout; dropped.");
                        _strings.RemoveAt(i);
                    }
                }

                if (_strings.Count == 0)
                    Config.EraseSectionKey(AudioSection, "mute");
                else if (_strings.Count != kept)
                    Config.SetValue(AudioSection, "mute", _strings);
            }
        }

        if (tree.Window is { } window)
        {
            if (Config.HasSectionKey(DisplaySection, "mode"))
                window.Mode = WindowMode;
            if (Config.HasSectionKey(DisplaySection, "vsync"))
                window.VSync = VSync;
            if (Config.HasSectionKey(DisplaySection, "maxFps"))
                window.MaxFps = MaxFps;
        }

        if (tree.Servers.Get<UiServer>() is { } ui)
            ui.UserScale = UiScale;
        if (Config.HasSectionKey(DisplaySection, "shadows") && tree.Servers.Render is { } render)
            ApplyShadows(render, Shadows);
        if (Language is { } language && LocaleId.Normalize(language) != Tr.CurrentLocale)
            Tr.SetLocale(language);
    }

    /// <summary>Erases <paramref name="section"/> (back to the project's defaults), applies the defaults live and raises <see cref="Changed"/>.</summary>
    public void Reset(UserSettingsSection section)
    {
        switch (section)
        {
            case UserSettingsSection.Audio:
                Config.GetStrings(AudioSection, "mute", _strings);
                Config.EraseSection(AudioSection);
                if (_tree?.Servers.Get<AudioServer>() is { } audio)
                {
                    foreach (var bus in audio.Buses)
                        bus.UserVolume = 1f;
                    foreach (var name in _strings)
                    {
                        if (audio.GetBus(name) is { } bus)
                            bus.Mute = false;
                    }
                }

                break;
            case UserSettingsSection.Input:
                Config.EraseSection(InputSection);
                if (_tree is { } tree)
                {
                    var map = tree.Input.Map;
                    foreach (var defaults in DefaultInput.Actions)
                    {
                        if (map.HasAction(defaults.Name))
                            map.ResetAction(defaults.Name, DefaultInput);
                    }
                }

                break;
            case UserSettingsSection.Display:
                Config.EraseSection(DisplaySection);
                if (_tree is { } displayTree)
                {
                    if (displayTree.Window is { } window)
                    {
                        window.Mode = WindowMode.Windowed;
                        window.Size = new System.Numerics.Vector2(_project.Window.Width, _project.Window.Height);
                        window.Mode = _project.Window.Mode;
                        window.VSync = _project.Window.VSync;
                        window.MaxFps = _project.Window.MaxFps;
                    }

                    if (displayTree.Servers.Get<UiServer>() is { } ui)
                        ui.UserScale = 1f;
                    if (displayTree.Servers.Render is { } render)
                        ApplyShadows(render, _project.Rendering.Shadows);
                }

                break;
            case UserSettingsSection.Language:
                Config.EraseSection(LocaleSection);
                if (_tree is not null)
                    ApplyLanguage(null);
                break;
            case UserSettingsSection.Game:
                foreach (var name in Config.GetSections())
                {
                    if (name is not (AudioSection or InputSection or DisplaySection or LocaleSection))
                        Config.EraseSection(name);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown settings section.");
        }

        Changed?.Invoke(section);
    }

    /// <summary>
    /// Writes <see cref="FilePath"/> durably (<see cref="ConfigFile.Save"/>: one backup). A failure is logged, not thrown: a
    /// full disk must not crash the game. Throws <see cref="InvalidOperationException"/> for in-memory settings.
    /// </summary>
    public void Save()
    {
        if (FilePath is not { } path)
            throw new InvalidOperationException("These settings have no file (create them with UserSettings.Load).");
        try
        {
            Config.Save(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[Settings] Could not save '{path}': {e.Message}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private AudioBus? Bus(string name) => _tree?.Servers.Get<AudioServer>()?.GetBus(name);

    private JsonObject? Volumes(bool create)
    {
        var node = Config.GetNode(AudioSection, "volume");
        if (node is JsonObject volumes)
            return volumes;
        if (node is not null && !create)
        {
            Config.WarnOnce("audio.volume", "audio.volume", "an object of bus volumes");
            return null;
        }

        if (!create)
            return null;
        volumes = new JsonObject();
        Config.SetNode(AudioSection, "volume", volumes);
        return volumes;
    }

    private static List<string> Keys(JsonObject o)
    {
        var keys = new List<string>(o.Count);
        foreach (var (key, _) in o)
            keys.Add(key);
        return keys;
    }

    private T EnumValue<T>(string section, string key, T @default) where T : struct, Enum
    {
        if (Config.GetString(section, key) is not { } text)
            return @default;
        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value) && !char.IsDigit(text[0]) && text[0] != '-')
            return value;
        Config.WarnOnce(section + "." + key, $"{section}.{key}", $"one of {string.Join(", ", Enum.GetNames<T>())}");
        return @default;
    }

    // Stores value (or erases the key when it is the default); false when nothing changed.
    private bool SetEnum<T>(string section, string key, T value, T @default, bool camelCase) where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(EnumValue(section, key, @default), value))
            return false;
        if (EqualityComparer<T>.Default.Equals(value, @default))
        {
            Config.EraseSectionKey(section, key);
        }
        else
        {
            var name = value.ToString();
            Config.SetValue(section, key, camelCase ? char.ToLowerInvariant(name[0]) + name[1..] : name);
        }

        return true;
    }

    private static void ApplyShadows(RenderServer render, ShadowQuality quality)
    {
        // To or from Off only before visuals exist; otherwise the value waits for the next start (ShadowsNeedRestart).
        if ((render.ShadowQuality == ShadowQuality.Off) == (quality == ShadowQuality.Off) || render.CanToggleShadows)
            render.ShadowQuality = quality;
    }

    private void ApplyLanguage(string? language)
    {
        var locale = language ?? Tr.ResolveStartupLocale(_project.Localization.DefaultLocale);
        if (LocaleId.Normalize(locale) != Tr.CurrentLocale)
            Tr.SetLocale(locale);
    }
}
