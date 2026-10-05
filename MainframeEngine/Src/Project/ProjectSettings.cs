using Silk.NET.Maths;

namespace MainframeEngine;

/// <summary>
/// A game project's settings — the contents of <c>project.mfproj</c> next to its <c>Content/</c> folder: name, main
/// scene, game assemblies, window, physics, input map, audio, localization, rendering, Steam and autoloads, plus the
/// engine version the project was created with. <see cref="GameHost"/> applies them; the editor edits them.
/// </summary>
/// <remarks>
/// The file is JSON in the scene-format conventions (UTF-8, LF, indented, comments and trailing commas tolerated,
/// only non-default values written) with a <c>format</c> number; older formats are upgraded step by step on load
/// (<see cref="ProjectSettingsFormat"/>). See docs/design/project-and-gamehost.md.
/// </remarks>
public sealed class ProjectSettings
{
    /// <summary>The project file's name.</summary>
    public const string FileName = "project.mfproj";

    /// <summary>Display name; the default window title and the user-data folder name.</summary>
    public string Name
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            field = value;
        }
    } = "Game";

    /// <summary>The scene <see cref="GameHost"/> starts with: a UID (<c>scn_…</c>) or a <c>Content/…</c> path.</summary>
    public string? MainScene { get; set; }

    /// <summary>
    /// The engine version the project was created with (or last upgraded to). New projects get
    /// <see cref="EngineInfo.Version"/>.
    /// </summary>
    public string EngineVersion { get; set; } = EngineInfo.Version;

    /// <summary>
    /// Game assemblies (names, without <c>.dll</c>) holding the project's node and resource types. <see cref="GameHost"/>
    /// loads them so their types register before any scene loads; the editor loads them into a collectible context.
    /// </summary>
    public List<string> Assemblies { get; } = [];

    public WindowSettings Window { get; } = new();

    public PhysicsProjectSettings Physics { get; } = new();

    /// <summary>The project's actions; <see cref="GameHost"/> installs it as the tree's <see cref="InputState.Map"/>.</summary>
    public InputMap Input
    {
        get;
        set => field = value ?? throw new ArgumentNullException(nameof(value));
    } = new();

    public AudioProjectSettings Audio { get; } = new();

    public LocalizationProjectSettings Localization { get; } = new();

    public RenderingProjectSettings Rendering { get; } = new();

    /// <summary>The game's Steam app id; 0 (default) leaves Steam alone (<see cref="EngineOptions.SteamAppId"/>).</summary>
    public uint SteamAppId { get; set; }

    /// <summary>Singleton nodes added under the root before the main scene, in order (Godot's autoloads).</summary>
    public List<AutoloadSettings> Autoloads { get; } = [];

    /// <summary>The file these settings were loaded from or last saved to (null for in-memory settings).</summary>
    public string? FilePath { get; internal set; }

    /// <summary>The folder holding <see cref="FilePath"/> (the project root), or null.</summary>
    public string? ProjectDirectory => FilePath is null ? null : Path.GetDirectoryName(FilePath);

    /// <summary>Reads <paramref name="path"/> (a <c>project.mfproj</c> file, or a folder containing one).</summary>
    public static ProjectSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = Directory.Exists(path) ? Path.Combine(path, FileName) : path;
        file = Path.GetFullPath(file);
        if (!File.Exists(file))
            throw new FileNotFoundException($"No project file at '{file}'.", file);
        var settings = ProjectSettingsFormat.Parse(File.ReadAllBytes(file), file);
        settings.FilePath = file;
        return settings;
    }

    /// <summary>
    /// The project file next to the application (<see cref="ContentPaths.BaseDirectory"/>), or null when there is none.
    /// </summary>
    public static ProjectSettings? LoadFromApplicationDirectory()
    {
        var file = Path.Combine(ContentPaths.BaseDirectory, FileName);
        return File.Exists(file) ? Load(file) : null;
    }

    /// <summary>Parses <c>project.mfproj</c> JSON (older formats are upgraded); <paramref name="source"/> names it in errors.</summary>
    public static ProjectSettings Parse(ReadOnlySpan<byte> json, string source = FileName) => ProjectSettingsFormat.Parse(json, source);

    /// <summary>Serializes to <c>project.mfproj</c> JSON (current format, LF line endings).</summary>
    public byte[] ToJson() => ProjectSettingsFormat.Write(this);

    /// <summary>Writes the settings to <paramref name="path"/> (a file, or a folder to put <see cref="FileName"/> in).</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = Path.GetFullPath(Directory.Exists(path) ? Path.Combine(path, FileName) : path);
        ResourceSaver.WriteAtomically(file, ToJson());
        FilePath = file;
    }

    /// <summary>
    /// Engine options from these settings (window, VSync, physics, audio, localization, Steam). Settings that need a
    /// running engine (frame cap, exposure, shadow quality, input, autoloads, main scene) are applied by <see cref="GameHost"/>.
    /// </summary>
    public EngineOptions ToEngineOptions() => new()
    {
        GameName = Window.Title ?? Name,
        WindowSize = new Vector2D<int>(Window.Width, Window.Height),
        IconPath = Window.Icon,
        VSync = Window.VSync,
        PhysicsTicksPerSecond = Physics.TicksPerSecond,
        Physics3D = Physics.Physics3D,
        Physics2D = Physics.Physics2D,
        SteamAppId = SteamAppId,
        Locale = Localization.DefaultLocale,
        Localization = Localization.ToOptions(),
        Audio = new AudioOptions
        {
            Enabled = Audio.Enabled,
            BusLayoutPath = Audio.BusLayout,
            SampleRate = Audio.SampleRate,
            BufferMilliseconds = Audio.BufferMilliseconds,
        },
    };
}

/// <summary>The <c>window</c> section of <c>project.mfproj</c>.</summary>
public sealed class WindowSettings
{
    public const int DefaultWidth = 1280;
    public const int DefaultHeight = 720;

    /// <summary>Window title; null uses <see cref="ProjectSettings.Name"/>.</summary>
    public string? Title { get; set; }

    public int Width
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultWidth;

    public int Height
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = DefaultHeight;

    public bool VSync { get; set; } = true;

    /// <summary>Frame-rate cap (<see cref="Engine.MaxFPS"/>) when VSync is off; 0 = unlimited.</summary>
    public int MaxFps
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>Window icon (a <c>Content/…</c> PNG), or null.</summary>
    public string? Icon { get; set; }

    /// <summary>
    /// How 2D content scales with the window (Godot's <c>display/window/stretch/mode</c>); the base size is
    /// <see cref="Width"/> × <see cref="Height"/>. <see cref="ContentScaleMode.Disabled"/> (default): one canvas unit per pixel.
    /// </summary>
    public ContentScaleMode StretchMode { get; set; }

    /// <summary>Godot's <c>display/window/stretch/aspect</c> (default keep).</summary>
    public ContentScaleAspect StretchAspect { get; set; } = ContentScaleAspect.Keep;

    /// <summary>Godot's <c>display/window/stretch/scale</c>.</summary>
    public float StretchScale
    {
        get;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The stretch scale must be positive and finite.");
            field = value;
        }
    } = 1f;

    /// <summary>Godot's <c>display/window/stretch/scale_mode</c>.</summary>
    public ContentScaleStretch StretchScaleMode { get; set; }
}

/// <summary>The <c>physics</c> section: the fixed tick and the 3D/2D server settings.</summary>
public sealed class PhysicsProjectSettings
{
    /// <summary>Fixed rate of <see cref="Node.OnPhysicsProcess"/> and the physics servers.</summary>
    public int TicksPerSecond
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 1000);
            field = value;
        }
    } = 60;

    /// <summary>Most fixed steps one frame may run (<see cref="SceneTree.MaxPhysicsStepsPerFrame"/>).</summary>
    public int MaxStepsPerFrame
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 5;

    public PhysicsSettings3D Physics3D { get; } = new();

    public PhysicsSettings2D Physics2D { get; } = new();
}

/// <summary>The <c>audio</c> section (<see cref="AudioOptions"/>).</summary>
public sealed class AudioProjectSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>The bus layout resource (<c>.mres</c> path or UID); null uses the built-in default layout.</summary>
    public string? BusLayout { get; set; } = AudioBusLayout.DefaultPath;

    public int SampleRate
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 8000);
            field = value;
        }
    } = 48000;

    public int BufferMilliseconds
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 10;
}

/// <summary>The <c>localization</c> section (the M9 <c>LocalizationOptions</c>).</summary>
public sealed class LocalizationProjectSettings
{
    /// <summary>The locale the game starts in (<c>es</c>, <c>pt_BR</c>); null picks the OS language when a catalog exists.</summary>
    public string? DefaultLocale { get; set; }

    /// <summary>The language msgids are written in; ends every fallback chain.</summary>
    public string SourceLocale { get; set; } = "en";

    /// <summary>Locales tried after the requested one and its parents, before <see cref="SourceLocale"/>.</summary>
    public List<string> Fallbacks { get; } = [];

    /// <summary>Folder holding one sub-folder per locale.</summary>
    public string LocaleDirectory { get; set; } = "Content/locale";

    /// <summary>Catalog domain (<c>{Domain}.mo</c>).</summary>
    public string Domain { get; set; } = "messages";

    /// <summary>
    /// The <see cref="Localization.Tr"/> options for this section (<see cref="EngineOptions.Localization"/>; the engine
    /// passes them to <see cref="Localization.Tr.Configure"/> with <see cref="DefaultLocale"/> before any game code runs).
    /// </summary>
    public Localization.LocalizationOptions ToOptions() => new()
    {
        Domain = Domain,
        LocaleDirectory = LocaleDirectory,
        SourceLocale = SourceLocale,
        FallbackLocales = [.. Fallbacks],
    };
}

/// <summary>The <c>rendering</c> section.</summary>
public sealed class RenderingProjectSettings
{
    /// <summary>Exposure the renderer starts with (<see cref="IVulkanContext.Exposure"/>).</summary>
    public float Exposure
    {
        get;
        set
        {
            if (!(value > 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Exposure must be positive and finite.");
            field = value;
        }
    } = IVulkanContext.DefaultExposure;

    /// <summary>
    /// Shadow quality (<see cref="RenderServer.ShadowQuality"/>). <see cref="ShadowQuality.Off"/> disables shadow maps;
    /// the other levels select the shadow budget — atlas size, PCF filter, cascades, map resolution
    /// (<see cref="ShadowQualitySettings.For"/>). Default <see cref="ShadowQuality.High"/>, the engine's defaults.
    /// </summary>
    public ShadowQuality Shadows { get; set; } = ShadowQuality.High;

    /// <summary>
    /// Opaque background of the 2D canvas (<see cref="CanvasServer.ClearColor"/>; a 2D game: Godot's viewport clear colour,
    /// gamma-space RGBA), or null (default) to draw the canvas over the 3D scene.
    /// </summary>
    public System.Numerics.Vector4? CanvasClearColor { get; set; }
}

/// <summary>
/// One autoload: a node created at startup and added under the root as <c>/root/{Name}</c>, before the main scene,
/// living for the whole game. Either a scene (<see cref="Scene"/>) or a registered node type (<see cref="Type"/>).
/// </summary>
public sealed class AutoloadSettings
{
    /// <summary>The node's name under the root (must be unique among autoloads).</summary>
    public required string Name { get; set; }

    /// <summary>A scene (UID or <c>Content/…</c> path) to instantiate.</summary>
    public string? Scene { get; set; }

    /// <summary>A registered node type name (<see cref="Serialization.TypeRegistry"/>) to create.</summary>
    public string? Type { get; set; }

    /// <summary>Disabled autoloads stay in the file but are not created.</summary>
    public bool Enabled { get; set; } = true;
}
