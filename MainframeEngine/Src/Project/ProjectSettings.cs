using Silk.NET.Maths;

namespace MainframeEngine;

/// <summary>
/// A game project's settings — the contents of <c>project.mfproj</c> next to its <c>Content/</c> folder: name, main
/// scene, game assemblies, demo build, window, physics, input map, audio, localization, rendering, Steam and autoloads, plus the
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

    /// <summary>The game's own version (Godot's <c>application/config/version</c>); empty when the project sets none.</summary>
    public string Version { get; set; } = "";

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

    /// <summary>
    /// This is the game's demo (a Steam demo: its own app on Steam, <see cref="SteamProjectSettings.DemoAppId"/>), not the
    /// full game. Game projects compile with <c>DEMO</c> defined when it is set (<c>#if DEMO</c>; the engine's
    /// <c>build/MainframeGame.props</c>, overridden per build with <c>-p:MainframeDemo=true|false</c>), and
    /// <see cref="GameHost"/> replaces it with the value the running game was built with.
    /// </summary>
    public bool IsDemo { get; set; }

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

    /// <summary>The game UI's scale (<see cref="UiServer.Scaling"/>, ADR 0181); <see cref="GameHost"/> applies it.</summary>
    public UiProjectSettings Ui { get; } = new();

    public SteamProjectSettings Steam { get; } = new();

    /// <summary>How <see cref="GameHost"/> loads the start scene: on a worker thread behind a loading screen (ADR 0183).</summary>
    public LoadingProjectSettings Loading { get; } = new();

    /// <summary>
    /// The Steam app id this build starts Steam with (<see cref="EngineOptions.SteamAppId"/>):
    /// <see cref="SteamProjectSettings.DemoAppId"/> for a demo (<see cref="IsDemo"/>), else
    /// <see cref="SteamProjectSettings.AppId"/>. 0 leaves Steam alone.
    /// </summary>
    public uint SteamAppId => IsDemo ? Steam.DemoAppId : Steam.AppId;

    /// <summary>Singleton nodes added under the root before the main scene, in order (Godot's autoloads).</summary>
    public List<AutoloadSettings> Autoloads { get; } = [];

    /// <summary>
    /// The editor's Run › Play Instances (Godot's "Customize Run Instances"): processes launched together, each with its
    /// own game arguments and start delay, e.g. a host and two tiled joiners. Empty: the command plays the main scene once.
    /// </summary>
    public List<PlayInstanceSettings> PlayInstances { get; } = [];

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
        ContentScale = Window.ContentScale,
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

    /// <summary>
    /// Pixels per point of <see cref="Width"/> × <see cref="Height"/> (<see cref="EngineOptions.ContentScale"/>). 0 (default):
    /// the size is in OS points (a Retina window is twice as many pixels). 1: the size is in pixels on every display, as in
    /// Godot (a 1920×1080 window is 960×540 points on a 2× screen).
    /// </summary>
    public float ContentScale
    {
        get;
        set
        {
            if (!(value >= 0) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The content scale must be 0 or positive and finite.");
            field = value;
        }
    }
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

/// <summary>The <c>steam</c> section: Steamworks app ids and start-up behaviour (docs/design/steamworks.md).</summary>
public sealed class SteamProjectSettings
{
    /// <summary>The full game's Steam app id; 0 (default) leaves Steam alone in full builds.</summary>
    public uint AppId { get; set; }

    /// <summary>
    /// The demo's Steam app id (a demo is a separate app on Steamworks), used by demo builds
    /// (<see cref="ProjectSettings.IsDemo"/>); 0 (default) leaves Steam alone in demo builds.
    /// </summary>
    public uint DemoAppId { get; set; }

    /// <summary>
    /// Development runs (started by the editor's Play, or a Debug build) write <c>steam_appid.txt</c> next to the game so
    /// Steam starts without the game being launched by Steam. Default true; shipped (Release) builds never write it.
    /// </summary>
    public bool DevAppIdFile { get; set; } = true;

    /// <summary>
    /// Shipped (Release) builds started outside Steam ask Steam to relaunch them through the Steam client and exit
    /// (<see cref="MainframeEngine.Steam.RestartAppIfNecessary"/>), so ownership and the overlay always apply. Default
    /// false; development runs never restart.
    /// </summary>
    public bool RestartThroughSteam { get; set; }
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
    /// Anti-aliasing of the main view (<see cref="IVulkanContext.AntiAliasing"/>, ADR 0154, ADR 0166): none (default),
    /// FXAA on the tonemapped image, or TAA on the HDR image; both under the UI.
    /// </summary>
    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.None;

    /// <summary>
    /// The sharpen after TAA, 0 (off) to 1 (<see cref="IVulkanContext.TaaSharpness"/>, ADR 0166; default
    /// <see cref="IVulkanContext.DefaultTaaSharpness"/>). Only with <see cref="AntiAliasing.Taa"/>.
    /// </summary>
    public float TaaSharpness
    {
        get;
        set
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "TAA sharpness must be between 0 and 1.");
            field = value;
        }
    } = IVulkanContext.DefaultTaaSharpness;

    /// <summary>
    /// Screen-space reflections of refracting water (<see cref="RenderServer.WaterSsr"/>, ADR 0173): Off (sky only), Low
    /// (default) or High.
    /// </summary>
    public WaterSsrQuality WaterSsr { get; set; } = WaterSsrQuality.Low;

    /// <summary>
    /// How the 3D view upscales from its render resolution (Godot's <c>scaling_3d_mode</c>; <see cref="IVulkanContext.Scaling3DMode"/>,
    /// ADR 0174): bilinear (default), FSR 1 or TAAU (which turns TAA on). Only with a <see cref="Scaling3DScale"/> below 1.
    /// </summary>
    public Scaling3DMode Scaling3DMode { get; set; } = Scaling3DMode.Bilinear;

    /// <summary>
    /// The 3D view's render scale, 0.25–1 (Godot's <c>scaling_3d_scale</c>; <see cref="IVulkanContext.Scaling3DScale"/>,
    /// ADR 0174; default 1, native). The 2D canvas and UI always render at the window's resolution.
    /// </summary>
    public float Scaling3DScale
    {
        get;
        set
        {
            if (!(value >= RenderScaling.MinScale && value <= RenderScaling.MaxScale))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The 3D scale must be between 0.25 and 1.");
            field = value;
        }
    } = RenderScaling.MaxScale;

    /// <summary>
    /// RCAS's attenuation after an FSR 1 upscale, in stops: 0 is the sharpest, 2 the softest (Godot's <c>fsr_sharpness</c>;
    /// <see cref="IVulkanContext.FsrSharpness"/>; default <see cref="RenderScaling.DefaultFsrSharpness"/>).
    /// </summary>
    public float FsrSharpness
    {
        get;
        set
        {
            if (!(value >= 0f && value <= RenderScaling.MaxFsrSharpness))
                throw new ArgumentOutOfRangeException(nameof(value), value, "FSR sharpness must be between 0 and 2.");
            field = value;
        }
    } = RenderScaling.DefaultFsrSharpness;

    /// <summary>
    /// Opaque background of the 2D canvas (<see cref="CanvasServer.ClearColor"/>; a 2D game: Godot's viewport clear colour,
    /// gamma-space RGBA), or null (default) to draw the canvas over the 3D scene.
    /// </summary>
    public System.Numerics.Vector4? CanvasClearColor { get; set; }
}

/// <summary>
/// The <c>ui</c> section: how every game UI (each <see cref="UiLayer"/> in <see cref="UiScaleMode.Project"/> mode, the
/// developer overlay, the RmlUi debugger) scales with the window — Unity's <c>CanvasScaler</c> (ADR 0181). New projects
/// scale with the screen from a 1920×1080 reference; projects from before format 3 keep
/// <see cref="UiScalingMode.ConstantPixelSize"/> (the migration writes it).
/// </summary>
public sealed class UiProjectSettings
{
    /// <summary><see cref="UiScalingMode.ScaleWithScreenSize"/> (default) or <see cref="UiScalingMode.ConstantPixelSize"/> (dp = the display's pixels per point).</summary>
    public UiScalingMode ScaleMode { get; set; } = UiScalingMode.ScaleWithScreenSize;

    /// <summary>The resolution (pixels) the UI is authored for: 1 dp = 1 pixel there. Default 1920×1080.</summary>
    public Vector2I ReferenceResolution
    {
        get;
        set
        {
            if (value.X < 1 || value.Y < 1 || value.X > 16384 || value.Y > 16384)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The reference resolution must be between 1×1 and 16384×16384.");
            field = value;
        }
    } = new(1920, 1080);

    /// <summary>0 = scale with the width, 1 = with the height (default), between: a log-space blend (<see cref="UiScaling.MatchWidthOrHeight"/>).</summary>
    public float MatchWidthOrHeight
    {
        get;
        set
        {
            if (!(value >= 0f && value <= 1f))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Match width or height must be between 0 and 1.");
            field = value;
        }
    } = 1f;

    /// <summary>Smallest UI scale (pixels per dp) when scaling with the screen; 0 (default) = no limit.</summary>
    public float MinScale
    {
        get;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value) || (MaxScale > 0f && value > MaxScale))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The minimum scale must be 0 (none) or positive, and not above the maximum.");
            field = value;
        }
    }

    /// <summary>Largest UI scale (pixels per dp) when scaling with the screen; 0 (default) = no limit.</summary>
    public float MaxScale
    {
        get;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value) || (value > 0f && value < MinScale))
                throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum scale must be 0 (none) or positive, and not below the minimum.");
            field = value;
        }
    }

    /// <summary>The <see cref="UiServer.Scaling"/> these settings describe.</summary>
    public UiScaling ToScaling() => new()
    {
        Mode = ScaleMode,
        ReferenceResolution = new System.Numerics.Vector2(ReferenceResolution.X, ReferenceResolution.Y),
        MatchWidthOrHeight = MatchWidthOrHeight,
        MinScale = MinScale,
        MaxScale = MaxScale,
    };
}

/// <summary>
/// The <c>loading</c> section (ADR 0183): <see cref="GameHost"/> loads the start scene with
/// <see cref="SceneTree.ChangeSceneToFileAsync"/> and shows a <see cref="LoadingScreen"/> while it loads, so the window
/// answers and animates from its first frame instead of freezing until the scene is ready.
/// </summary>
public sealed class LoadingProjectSettings
{
    /// <summary>Load the start scene asynchronously with a loading screen (default); false loads it synchronously, as before.</summary>
    public bool Async { get; set; } = true;

    /// <summary>
    /// The loading screen's document (<c>Content/…</c> <c>.rml</c> binding the <c>loading</c> data model); null (default) is
    /// the engine's (<see cref="LoadingScreen.DefaultSource"/>), an empty string shows none.
    /// </summary>
    public string? Screen { get; set; }
}

/// <summary>One process of <see cref="ProjectSettings.PlayInstances"/>.</summary>
public sealed class PlayInstanceSettings
{
    /// <summary>The instance's label in the editor (its toolbar chip and log prefix).</summary>
    public required string Label { get; set; }

    /// <summary>The game's own arguments (passed after <c>++</c>, <see cref="GameHost.UserArgs"/>), e.g. <c>--host --tile 0:2</c>.</summary>
    public List<string> Arguments { get; } = [];

    /// <summary>Seconds after the first instance starts (a joiner waits for the host to listen).</summary>
    public double DelaySeconds { get; set; }
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
