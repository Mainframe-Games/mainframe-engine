using System.Numerics;

namespace MainframeEngine;

/// <summary>Where ambient light comes from (Godot's <c>Environment.AmbientSource</c>, the two sources the engine has).</summary>
public enum AmbientSource : byte
{
    /// <summary><see cref="WorldEnvironment.AmbientColor"/> everywhere.</summary>
    Color,

    /// <summary>The sky's irradiance (image-based lighting, ADR 0150); the colour while there is no sky.</summary>
    Sky,
}

/// <summary>Where reflections come from (Godot's <c>Environment.ReflectionSource</c>).</summary>
public enum ReflectedLightSource : byte
{
    /// <summary>The sky when the world has one, else a uniform environment of the ambient colour.</summary>
    Background,

    /// <summary>No reflected light.</summary>
    Disabled,

    /// <summary>The sky (the ambient colour while there is no sky).</summary>
    Sky,
}

/// <summary>
/// Sky, ambient and reflected light, wind and fog for its viewport's world, and the post-processing look it asks the
/// renderer for (Godot's <c>WorldEnvironment</c>). The sky is described by a <see cref="MainframeEngine.Sky"/> resource;
/// the render server builds the matching <see cref="SkyEnvironment"/> on first use and rebuilds it when the sky's mode or
/// images change. A physical sky's sun is the world's first <see cref="DirectionalLight3D"/> (ADR 0154). Only the first
/// environment in a world is used. With a sky, PBR materials reflect it and <see cref="AmbientSource"/> can light the world
/// with it: the render server captures it into image-based lighting cubes (<see cref="SkyRadiance"/>, ADR 0150) when it
/// changes. Tonemap, auto exposure, glow, light shafts, SSAO and the colour adjustments live in a
/// <see cref="PostProcessProfile"/> (<see cref="PostProcess"/>, ADR 0169), the lens in <see cref="CameraAttributes"/>.
/// </summary>
[EditorIcon("world", Family = EditorIconFamily.Space3D)]
[SerializedVersion(2)]
public class WorldEnvironment : Node, IRenderResourceOwner
{
    private World3D? _world;
    private Vector3 _ambientColor = LightEnvironment.DefaultAmbientColor;
    private SkyEnvironment? _skyEnvironment;
    private RenderServer? _server;
    private SkyEnvironmentType _builtMode;
    private string? _builtPanorama;
    private string[]? _builtFaces;
    private SkyRadiance? _radiance;

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

    /// <summary>
    /// Where ambient light comes from (Godot's <c>ambient_light_source</c>): <see cref="AmbientColor"/> (the default) or
    /// the sky's irradiance. Applies to Blinn-Phong and PBR materials alike.
    /// </summary>
    [Export]
    public AmbientSource AmbientSource { get; set; }

    /// <summary>Scales the ambient light, colour or sky (Godot's <c>ambient_light_energy</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float AmbientEnergy { get; set; } = 1f;

    /// <summary>
    /// Where PBR reflections come from (Godot's <c>reflected_light_source</c>): by default the sky, captured into a
    /// prefiltered cube; without a sky, a uniform environment of the ambient colour.
    /// </summary>
    [Export]
    public ReflectedLightSource ReflectedLightSource { get; set; }

    /// <summary>
    /// The post-processing look (ADR 0169): tonemap, auto exposure, glow, light shafts, SSAO and the colour adjustments —
    /// a <c>.mres</c> shared between scenes, or inline. Null: the engine's own tonemap and no effects.
    /// </summary>
    [ExportGroup("Post-Processing")]
    [Export]
    public PostProcessProfile? PostProcess { get; set; }

    /// <summary>
    /// The world's lens (Godot's <c>camera_attributes</c>, ADR 0168): depth of field and film effects. A
    /// <see cref="Camera3D.Attributes"/> on the current camera replaces it.
    /// </summary>
    [Export]
    public CameraAttributesPractical? CameraAttributes { get; set; }

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

    /// <summary>
    /// The packed post-processing settings this environment asks the renderer for (ADR 0124, ADR 0165, ADR 0168, ADR 0169):
    /// <see cref="PostProcess"/>'s, or the engine defaults without a profile, with <see cref="CameraAttributes"/>' depth of
    /// field and film effects. A struct copy (no allocation); the tree's root world's is used by the main view, a
    /// post-processed <see cref="SubViewport"/>'s world's by that view.
    /// </summary>
    public PostProcessSettings PostProcessSettings
    {
        get
        {
            var settings = PostProcess?.Settings ?? PostProcessSettings.Default;
            return CameraAttributes is { } attributes ? attributes.ApplyTo(settings) : settings;
        }
    }

    /// <summary>
    /// Scenes written before ADR 0169 (version 1) kept the post-processing settings on the environment itself: they load
    /// into an inline <see cref="PostProcessProfile"/> on <see cref="PostProcess"/>, which the next save writes as a
    /// sub-resource.
    /// </summary>
    [SerializedMigration(1)]
    internal static void MovePostProcessIntoProfile(Serialization.PropertyBag properties)
    {
        Serialization.PropertyBag? moved = null;
        foreach (var name in PostProcessProfile.MovedPropertyNames)
        {
            if (!properties.TryGet(name, out var value))
                continue;
            (moved ??= new Serialization.PropertyBag()).Set(name, value);
            properties.Remove(name);
        }

        if (moved is not null && !properties.Contains(nameof(PostProcess)))
            properties.SetInlineResource(nameof(PostProcess), nameof(PostProcessProfile), moved);
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

    internal void DrawSky(RenderServer server, ICamera camera) => SyncSky(server)?.Draw(camera);

    /// <summary>Whether the sky lights the world: there is one, and ambient or reflected light comes from it.</summary>
    private bool UsesSkyLighting => Sky is not null &&
                                    (AmbientSource == AmbientSource.Sky || ReflectedLightSource != ReflectedLightSource.Disabled);

    /// <summary>
    /// Captures the sky into the image-based lighting cubes when it changed (<see cref="SkyRadiance.Update"/>). Called by
    /// the render server with the frame's command buffer and no render pass active, before the world's views draw.
    /// </summary>
    internal void UpdateSkyLighting(RenderServer server, Silk.NET.Vulkan.CommandBuffer cb)
    {
        if (!UsesSkyLighting || server.Vulkan is not { } vk || SyncSky(server) is not { CanDraw: true } env)
            return;
        _radiance ??= new SkyRadiance(vk);
        _radiance.Update(cb, env);
    }

    /// <summary>The captured sky to bind for this world's views, or null (no sky lighting yet, or none wanted).</summary>
    internal EnvironmentMaps? SkyLightingMaps => UsesSkyLighting && _radiance is { IsBaked: true } radiance ? radiance.Maps : null;

    /// <summary>The image-based lighting cubes (null until the sky lights the world).</summary>
    internal SkyRadiance? Radiance => _radiance;

    /// <summary>
    /// The light-UBO environment flags for this world (<c>kEnv*</c> in <c>lights_data.slang</c>):
    /// <paramref name="maps"/> is what <see cref="SkyLightingMaps"/> returned.
    /// </summary>
    internal int EnvironmentFlags(EnvironmentMaps? maps)
    {
        var flags = 0;
        if (ReflectedLightSource == ReflectedLightSource.Disabled)
            flags |= LightEnvironment.EnvironmentNoSpecular;
        else if (maps is not null)
            flags |= LightEnvironment.EnvironmentSkySpecular;
        if (maps is not null && AmbientSource == AmbientSource.Sky)
            flags |= LightEnvironment.EnvironmentSkyDiffuse;
        return flags;
    }

    // Builds or rebuilds the sky's GPU objects and copies the resource's parameters into them; null without a sky.
    private SkyEnvironment? SyncSky(RenderServer server) => Sky is { } sky ? SyncSky(server, sky) : null;

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
        _radiance?.Dispose();
        _radiance = null;
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
