using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Sky, ambient light, wind, fog, tonemap, glow and auto exposure for its viewport's world (Godot's
/// <c>WorldEnvironment</c>). The sky is described by a <see cref="MainframeEngine.Sky"/> resource; the render server builds
/// the matching <see cref="SkyEnvironment"/> on first use and rebuilds it when the sky's mode or images change. A
/// physical sky's sun is the world's first <see cref="DirectionalLight3D"/> (ADR 0154). Only the first environment in a
/// world is used.
/// </summary>
[EditorIcon("world", Family = EditorIconFamily.Space3D)]
public class WorldEnvironment : Node, IRenderResourceOwner
{
    private World3D? _world;
    private Vector3 _ambientColor = LightEnvironment.DefaultAmbientColor;
    private SkyEnvironment? _skyEnvironment;
    private RenderServer? _server;
    private SkyEnvironmentType _builtMode;
    private string? _builtPanorama;
    private string[]? _builtFaces;
    private PostProcessSettings _post = PostProcessSettings.Default;

    /// <summary>The sky drawn behind everything; null draws no sky (the clear color shows).</summary>
    [Export]
    public Sky? Sky { get; set; }

    /// <summary>
    /// Ambient light color for every lit surface in the world, authored in sRGB (converted to linear by the
    /// <see cref="LightEnvironment"/>). Defaults to <see cref="LightEnvironment.DefaultAmbientColor"/>.
    /// </summary>
    [Export]
    public Vector3 AmbientColor
    {
        get => _ambientColor;
        set
        {
            _ambientColor = value;
            if (_world is not null && ReferenceEquals(_world.Environment, this))
                _world.Lights.AmbientColor = value;
        }
    }

    /// <summary>The tonemap and glow this environment asks the renderer for (ADR 0124); the tree's root world's is used.</summary>
    public PostProcessSettings PostProcess => _post;

    [ExportGroup("Wind")]
    /// <summary>World direction the wind blows towards (normalized when packed). Foliage, grass and water read it.</summary>
    [Export]
    public Vector3 WindDirection { get; set; } = new(1f, 0f, 0.3f);

    /// <summary>Wind strength (0 = calm, 1 = a steady breeze).</summary>
    [Export(Range = "0,4,0.01")]
    public float WindStrength { get; set; }

    /// <summary>Main sway frequency in Hz.</summary>
    [Export(Range = "0,4,0.01")]
    public float WindFrequency { get; set; } = 0.5f;

    /// <summary>How much gusts vary across space and time (0..1).</summary>
    [Export(Range = "0,1,0.01")]
    public float WindTurbulence { get; set; } = 0.4f;

    [ExportGroup("Fog")]
    /// <summary>Distance and height fog over every lit surface (Godot's <c>fog_enabled</c>).</summary>
    [Export]
    public bool FogEnabled { get; set; }

    /// <summary>Fog colour where it is not lit by the sun, authored in sRGB (Godot's <c>fog_light_color</c>).</summary>
    [Export]
    public Vector3 FogLightColor { get; set; } = new(0.52f, 0.60f, 0.68f);

    /// <summary>Fog density per metre (Godot's <c>fog_density</c>).</summary>
    [Export(Range = "0,1,0.0001")]
    public float FogDensity { get; set; } = 0.01f;

    /// <summary>World height where height fog starts thinning (Godot's <c>fog_height</c>).</summary>
    [Export(Range = "-1024,1024,0.1")]
    public float FogHeight { get; set; }

    /// <summary>How fast fog thins above <see cref="FogHeight"/>, per metre (Godot's <c>fog_height_density</c>); 0 = uniform fog.</summary>
    [Export(Range = "0,4,0.001")]
    public float FogHeightDensity { get; set; }

    /// <summary>How much the sun colours fog looking towards it (Godot's <c>fog_sun_scatter</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float FogSunScatter { get; set; }

    /// <summary>The wind and fog packed for the per-frame shader data (linear colour, normalized direction).</summary>
    public FrameEnvironment FrameEnvironment
    {
        get
        {
            var dir = WindDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(WindDirection) : Vector3.UnitX;
            var fog = ColorSpace.SrgbToLinear(FogLightColor);
            return new FrameEnvironment(
                new Vector4(dir, WindStrength),
                new Vector4(WindFrequency, WindTurbulence, 24f, 0f),
                new Vector4(fog, FogEnabled ? 1f : 0f),
                new Vector4(FogDensity, FogHeight, FogHeightDensity, FogSunScatter));
        }
    }

    [ExportGroup("Tonemap")]
    /// <summary>The tonemap curve (ADR 0124): the engine's ACES fit by default, or Godot 4.7's ACES.</summary>
    [Export]
    public Tonemapper Tonemapper
    {
        get => _post.Tonemapper;
        set => _post = _post with { Tonemapper = value };
    }

    /// <summary>Exposure for the Godot curve (Godot's <c>tonemap_exposure</c>); the engine curve uses the project exposure.</summary>
    [Export(Range = "0,16,0.01")]
    public float TonemapExposure
    {
        get => _post.TonemapExposure;
        set => _post = _post with { TonemapExposure = value };
    }

    /// <summary>White point for the Godot curve (Godot's <c>tonemap_white</c>; ACES uses at least 1).</summary>
    [Export(Range = "0,16,0.01")]
    public float TonemapWhite
    {
        get => _post.TonemapWhite;
        set => _post = _post with { TonemapWhite = value };
    }

    [ExportGroup("Glow")]
    /// <summary>Godot 4.7's glow (ADR 0124): blurred bright areas added back before the tonemap.</summary>
    [Export]
    public bool GlowEnabled
    {
        get => _post.GlowEnabled;
        set => _post = _post with { GlowEnabled = value };
    }

    /// <summary>Weight of the half-resolution blur level (Godot's <c>glow_levels/1</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel1
    {
        get => _post.GlowLevel1;
        set => _post = _post with { GlowLevel1 = value };
    }

    /// <summary>Weight of the 1/4-resolution level (<c>glow_levels/2</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel2
    {
        get => _post.GlowLevel2;
        set => _post = _post with { GlowLevel2 = value };
    }

    /// <summary>Weight of the 1/8-resolution level (<c>glow_levels/3</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel3
    {
        get => _post.GlowLevel3;
        set => _post = _post with { GlowLevel3 = value };
    }

    /// <summary>Weight of the 1/16-resolution level (<c>glow_levels/4</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel4
    {
        get => _post.GlowLevel4;
        set => _post = _post with { GlowLevel4 = value };
    }

    /// <summary>Weight of the 1/32-resolution level (<c>glow_levels/5</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel5
    {
        get => _post.GlowLevel5;
        set => _post = _post with { GlowLevel5 = value };
    }

    /// <summary>Weight of the 1/64-resolution level (<c>glow_levels/6</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel6
    {
        get => _post.GlowLevel6;
        set => _post = _post with { GlowLevel6 = value };
    }

    /// <summary>Weight of the 1/128-resolution level (<c>glow_levels/7</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float GlowLevel7
    {
        get => _post.GlowLevel7;
        set => _post = _post with { GlowLevel7 = value };
    }

    /// <summary>Divide the level weights by their sum (Godot's <c>glow_normalized</c>).</summary>
    [Export]
    public bool GlowNormalized
    {
        get => _post.GlowNormalized;
        set => _post = _post with { GlowNormalized = value };
    }

    /// <summary>Godot's <c>glow_intensity</c>.</summary>
    [Export(Range = "0,8,0.01")]
    public float GlowIntensity
    {
        get => _post.GlowIntensity;
        set => _post = _post with { GlowIntensity = value };
    }

    /// <summary>Multiplies every blur level (Godot's <c>glow_strength</c>).</summary>
    [Export(Range = "0,2,0.01")]
    public float GlowStrength
    {
        get => _post.GlowStrength;
        set => _post = _post with { GlowStrength = value };
    }

    /// <summary>The glow share in Mix mode (Godot's <c>glow_mix</c>).</summary>
    [Export(Range = "0,1,0.001")]
    public float GlowMix
    {
        get => _post.GlowMix;
        set => _post = _post with { GlowMix = value };
    }

    /// <summary>Minimum glow below the threshold (Godot's <c>glow_bloom</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float GlowBloom
    {
        get => _post.GlowBloom;
        set => _post = _post with { GlowBloom = value };
    }

    /// <summary>How glow combines with the scene (Godot's <c>glow_blend_mode</c>, default Screen).</summary>
    [Export]
    public GlowBlendMode GlowBlendMode
    {
        get => _post.GlowBlendMode;
        set => _post = _post with { GlowBlendMode = value };
    }

    /// <summary>Brightness where glow starts (Godot's <c>glow_hdr_threshold</c>).</summary>
    [Export(Range = "0,4,0.01")]
    public float GlowHdrThreshold
    {
        get => _post.GlowHdrThreshold;
        set => _post = _post with { GlowHdrThreshold = value };
    }

    /// <summary>Range over which glow fades in above the threshold (Godot's <c>glow_hdr_scale</c>).</summary>
    [Export(Range = "0,4,0.01")]
    public float GlowHdrScale
    {
        get => _post.GlowHdrScale;
        set => _post = _post with { GlowHdrScale = value };
    }

    /// <summary>Brightest a glow sample can be (Godot's <c>glow_hdr_luminance_cap</c>).</summary>
    [Export(Range = "0,256,0.01")]
    public float GlowHdrLuminanceCap
    {
        get => _post.GlowHdrLuminanceCap;
        set => _post = _post with { GlowHdrLuminanceCap = value };
    }

    [ExportGroup("Auto Exposure")]
    /// <summary>Eye adaptation (ADR 0154): the exposure follows the scene's average luminance over time.</summary>
    [Export]
    public bool AutoExposureEnabled
    {
        get => _post.AutoExposureEnabled;
        set => _post = _post with { AutoExposureEnabled = value };
    }

    /// <summary>The luminance an average scene is exposed to (middle grey; Godot's <c>auto_exposure_scale</c>).</summary>
    [Export(Range = "0.01,16,0.01")]
    public float AutoExposureScale
    {
        get => _post.AutoExposureScale;
        set => _post = _post with { AutoExposureScale = value };
    }

    /// <summary>How fast the exposure adapts, per second (Godot's <c>auto_exposure_speed</c>).</summary>
    [Export(Range = "0.01,64,0.01")]
    public float AutoExposureSpeed
    {
        get => _post.AutoExposureSpeed;
        set => _post = _post with { AutoExposureSpeed = value };
    }

    /// <summary>The darkest average luminance adapted to (caps how much a dark scene is brightened).</summary>
    [Export(Range = "0.0001,64,0.0001")]
    public float AutoExposureMinLuminance
    {
        get => _post.AutoExposureMinLuminance;
        set => _post = _post with { AutoExposureMinLuminance = value };
    }

    /// <summary>The brightest average luminance adapted to (caps how much a bright scene is darkened).</summary>
    [Export(Range = "0.0001,1024,0.01")]
    public float AutoExposureMaxLuminance
    {
        get => _post.AutoExposureMaxLuminance;
        set => _post = _post with { AutoExposureMaxLuminance = value };
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.AddEnvironment(this);
    }

    protected override void OnExitTree()
    {
        _world?.RemoveEnvironment(this);
        _world = null;
        base.OnExitTree();
    }

    /// <summary>
    /// The sky's offscreen work for this frame (the physical sky's LUTs; ADR 0154), recorded by the render server with no
    /// render pass active before anything draws the sky.
    /// </summary>
    internal void PrepareSky(RenderServer server, Silk.NET.Vulkan.CommandBuffer cb)
    {
        if (Sky is { } sky)
            SyncSky(server, sky).Prepare(cb);
    }

    /// <summary>The sky environment built for <see cref="Sky"/> (null before the first frame draws it; tests).</summary>
    internal SkyEnvironment? SkyEnvironment => _skyEnvironment;

    internal void DrawSky(RenderServer server, ICamera camera)
    {
        if (Sky is not { } sky)
            return;

        SyncSky(server, sky).Draw(camera);
    }

    // Builds the sky environment when the mode or images changed and copies the parameters into it.
    private SkyEnvironment SyncSky(RenderServer server, Sky sky)
    {
        if (_skyEnvironment is null
            || _builtMode != sky.Mode
            || !string.Equals(_builtPanorama, sky.Panorama, StringComparison.Ordinal)
            || !ReferenceEquals(_builtFaces, sky.CubemapFaces))
            BuildSky(server, sky);

        var env = _skyEnvironment!;
        env.SkyColor = sky.SkyColor;
        env.HorizonColor = sky.HorizonColor;
        env.GroundColor = sky.GroundColor;
        env.SunDirection = sky.SunDirection;
        env.SunColor = sky.SunColor;
        env.SunIntensity = sky.SunIntensity;
        env.SunAngularRadius = sky.SunAngularRadius;
        env.HorizonSharpness = sky.HorizonSharpness;
        if (sky.Mode == SkyEnvironmentType.Physical)
            SyncPhysicalSun(env, sky);
        return env;
    }

    // The physical sky's sun is the world's first DirectionalLight3D (Godot's rule for sky shaders): towards its +Z axis,
    // with its colour and energy as the sun's illuminance. Without one, Sky.SunDirection and Sky.SunColor at energy 1.
    private void SyncPhysicalSun(SkyEnvironment env, Sky sky)
    {
        env.Physical = sky.PhysicalSettings;
        if (_world?.Lights.DirectionalLights is [var light, ..])
        {
            env.SunDirection = -light.Direction;
            env.SunColor = light.Color;
            env.SunIntensity = light.Intensity;
        }
        else
        {
            env.SunIntensity = 1f;
        }
    }

    private void BuildSky(RenderServer server, Sky sky)
    {
        ReleaseSky();
        _skyEnvironment = sky.Mode switch
        {
            SkyEnvironmentType.Panoramic => new SkyPanoramic(server.Renderer, sky.Panorama),
            SkyEnvironmentType.Cubemap => new SkyCubemap(server.Renderer,
                sky.CubemapFaces ?? throw new InvalidOperationException("A cubemap sky needs 6 CubemapFaces.")),
            SkyEnvironmentType.Physical => new SkyPhysical(server.Renderer),
            _ => new SkyProcedural(server.Renderer),
        };
        _builtMode = sky.Mode;
        _builtPanorama = sky.Panorama;
        _builtFaces = sky.CubemapFaces;
        _server = server;
        server.Track(this);
    }

    private void ReleaseSky()
    {
        _skyEnvironment?.Dispose();
        _skyEnvironment = null;
        _server?.Untrack(this);
        _server = null;
    }

    void IRenderResourceOwner.ReleaseRenderResourcesForShutdown() => ReleaseSky();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            ReleaseSky();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Sky settings for a <see cref="WorldEnvironment"/>: procedural gradient + sun, an equirectangular panorama,
/// or a cubemap (see <see cref="SkyEnvironment"/>). Defaults match <see cref="SkyEnvironment"/>'s.
/// </summary>
[EditorIcon("cloud")]
public class Sky : Resource
{
    [Export]
    public SkyEnvironmentType Mode { get; set; } = SkyEnvironmentType.Procedural;

    /// <summary>Equirectangular image for <see cref="SkyEnvironmentType.Panoramic"/>.</summary>
    [Export(File = "*.png,*.jpg,*.jpeg,*.hdr")]
    public string? Panorama { get; set; }

    /// <summary>Six images (+X, -X, +Y, -Y, +Z, -Z) for <see cref="SkyEnvironmentType.Cubemap"/>.</summary>
    [Export(File = "*.png,*.jpg,*.jpeg")]
    public string[]? CubemapFaces { get; set; }

    [ExportGroup("Procedural")]
    [Export]
    public Vector3 SkyColor { get; set; } = new(0.18f, 0.48f, 0.87f);

    [Export]
    public Vector3 HorizonColor { get; set; } = new(0.70f, 0.85f, 1.00f);

    [Export]
    public Vector3 GroundColor { get; set; } = SkyEnvironment.DefaultGroundColor;

    [Export]
    public float HorizonSharpness { get; set; } = 6f;

    [ExportGroup("Sun")]
    [Export]
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new Vector3(0.3f, 1f, 0.5f));

    [Export]
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);

    [Export(Range = "0,100,0.01")]
    public float SunIntensity { get; set; } = 20f;

    /// <summary>Angular radius of the sun disk in degrees.</summary>
    [Export(Range = "0,10,0.01")]
    public float SunAngularRadius { get; set; } = 0.53f;

    // Physical (ADR 0154): Godot's PhysicalSkyMaterial names; GroundColor (above) is the ground albedo.

    /// <summary>Rayleigh scattering strength (Godot's <c>rayleigh_coefficient</c>; 2 = Earth).</summary>
    [ExportGroup("Physical")]
    [Export(Range = "0,64,0.01")]
    public float RayleighCoefficient { get; set; } = PhysicalSkySettings.DefaultRayleighCoefficient;

    /// <summary>Tint of Rayleigh scattering (Godot's <c>rayleigh_color</c>).</summary>
    [Export]
    public Vector3 RayleighColor { get; set; } = PhysicalSkySettings.DefaultRayleighColor;

    /// <summary>Mie (haze) scattering strength (Godot's <c>mie_coefficient</c>; 0.005 = Earth).</summary>
    [Export(Range = "0,1,0.0001")]
    public float MieCoefficient { get; set; } = PhysicalSkySettings.DefaultMieCoefficient;

    /// <summary>How tightly haze glows around the sun, −1..1 (Godot's <c>mie_eccentricity</c>).</summary>
    [Export(Range = "-1,1,0.01")]
    public float MieEccentricity { get; set; } = 0.8f;

    /// <summary>Tint of Mie scattering (Godot's <c>mie_color</c>).</summary>
    [Export]
    public Vector3 MieColor { get; set; } = PhysicalSkySettings.DefaultMieColor;

    /// <summary>Haze density multiplier (Godot's <c>turbidity</c>; 10 = Earth).</summary>
    [Export(Range = "0,1000,0.01")]
    public float Turbidity { get; set; } = PhysicalSkySettings.DefaultTurbidity;

    /// <summary>Multiplies the sun disc's size (Godot's <c>sun_disk_scale</c>).</summary>
    [Export(Range = "0,360,0.01")]
    public float SunDiskScale { get; set; } = 1f;

    /// <summary>Multiplies the sky's and the sun disc's brightness (Godot's <c>energy_multiplier</c>).</summary>
    [Export(Range = "0,128,0.01")]
    public float EnergyMultiplier { get; set; } = 1f;

    /// <summary>The camera's height above sea level, in metres.</summary>
    [Export(Range = "0,20000,1")]
    public float AltitudeMeters { get; set; } = 300f;

    /// <summary>The physical-mode properties as the renderer's settings.</summary>
    public PhysicalSkySettings PhysicalSettings => new()
    {
        RayleighCoefficient = RayleighCoefficient,
        RayleighColor = RayleighColor,
        MieCoefficient = MieCoefficient,
        MieEccentricity = MieEccentricity,
        MieColor = MieColor,
        Turbidity = Turbidity,
        SunDiskScale = SunDiskScale,
        GroundColor = GroundColor,
        EnergyMultiplier = EnergyMultiplier,
        AltitudeMeters = AltitudeMeters,
    };
}
