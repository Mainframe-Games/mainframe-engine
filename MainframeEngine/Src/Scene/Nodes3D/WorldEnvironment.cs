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
/// Sky, ambient light, wind, fog, tonemap, glow, auto exposure, light shafts and SSAO for its viewport's world (Godot's
/// <c>WorldEnvironment</c>). The sky is described by a <see cref="MainframeEngine.Sky"/> resource; the render server builds
/// the matching <see cref="SkyEnvironment"/> on first use and rebuilds it when the sky's mode or images change. A
/// physical sky's sun is the world's first <see cref="DirectionalLight3D"/> (ADR 0154). Only the first environment in a
/// world is used. With a sky, PBR materials reflect it and <see cref="AmbientSource"/> can light the world with it: the
/// render server captures it into image-based lighting cubes (<see cref="SkyRadiance"/>, ADR 0150) when it changes.
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
    /// The tonemap, glow, auto exposure, light shafts, SSAO, adjustments and (from <see cref="CameraAttributes"/>) depth of
    /// field and film effects this environment asks the renderer for (ADR 0124, ADR 0165, ADR 0168); the tree's root
    /// world's is used.
    /// </summary>
    public PostProcessSettings PostProcess => CameraAttributes is { } attributes ? attributes.ApplyTo(_post) : _post;

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
    /// <summary>
    /// The tonemap curve (ADR 0124, ADR 0168): the engine's ACES fit by default, Godot 4.7's ACES, or Godot 4.4's linear,
    /// Reinhard, filmic and AgX.
    /// </summary>
    [Export]
    public Tonemapper Tonemapper
    {
        get => _post.Tonemapper;
        set => _post = _post with { Tonemapper = value };
    }

    /// <summary>Exposure for the Godot curves (Godot's <c>tonemap_exposure</c>); the engine curve uses the project exposure.</summary>
    [Export(Range = "0,16,0.01")]
    public float TonemapExposure
    {
        get => _post.TonemapExposure;
        set => _post = _post with { TonemapExposure = value };
    }

    /// <summary>White point for the Godot curves (Godot's <c>tonemap_white</c>; ACES, Reinhard and filmic use at least 1; AgX ignores it).</summary>
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

    /// <summary>How glow is built (ADR 0168): Godot's chain, or the 13-tap / tent chain with an anti-firefly first level.</summary>
    [Export]
    public GlowQuality GlowQuality
    {
        get => _post.GlowQuality;
        set => _post = _post with { GlowQuality = value };
    }

    [ExportGroup("Adjustments")]
    /// <summary>Colour adjustments after the tonemap (Godot's <c>adjustment_enabled</c>, ADR 0168).</summary>
    [Export]
    public bool AdjustmentEnabled
    {
        get => _post.AdjustmentEnabled;
        set => _post = _post with { AdjustmentEnabled = value };
    }

    /// <summary>Godot's <c>adjustment_brightness</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentBrightness
    {
        get => _post.AdjustmentBrightness;
        set => _post = _post with { AdjustmentBrightness = value };
    }

    /// <summary>Godot's <c>adjustment_contrast</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentContrast
    {
        get => _post.AdjustmentContrast;
        set => _post = _post with { AdjustmentContrast = value };
    }

    /// <summary>Godot's <c>adjustment_saturation</c> (1 = unchanged).</summary>
    [Export(Range = "0.01,8,0.01")]
    public float AdjustmentSaturation
    {
        get => _post.AdjustmentSaturation;
        set => _post = _post with { AdjustmentSaturation = value };
    }

    /// <summary>Godot's <c>adjustment_color_correction</c>: a 3D LUT (a <c>.cube</c> file) the display colour is looked up in.</summary>
    [Export]
    public Texture3D? AdjustmentColorCorrection
    {
        get => _post.AdjustmentColorCorrection;
        set => _post = _post with { AdjustmentColorCorrection = value };
    }

    /// <summary>How much of the LUT's result is used (engine; 1 = all of it).</summary>
    [Export(Range = "0,1,0.01")]
    public float AdjustmentColorCorrectionStrength
    {
        get => _post.AdjustmentColorCorrectionStrength;
        set => _post = _post with { AdjustmentColorCorrectionStrength = value };
    }

    [ExportGroup("Camera")]
    /// <summary>
    /// The world's lens (Godot's <c>camera_attributes</c>, ADR 0168): depth of field and film effects. A
    /// <see cref="Camera3D.Attributes"/> on the current camera replaces it.
    /// </summary>
    [Export]
    public CameraAttributesPractical? CameraAttributes { get; set; }

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

    [ExportGroup("Light Shafts")]
    /// <summary>
    /// Screen-space light shafts (ADR 0160): the sky around the sun, the world's first <see cref="DirectionalLight3D"/>,
    /// streaked towards it where leaves, trunks and buildings leave gaps.
    /// </summary>
    [Export]
    public bool LightShaftsEnabled
    {
        get => _post.LightShaftsEnabled;
        set => _post = _post with { LightShaftsEnabled = value };
    }

    /// <summary>How much of the shafts is added to the scene.</summary>
    [Export(Range = "0,16,0.01")]
    public float LightShaftsIntensity
    {
        get => _post.LightShaftsIntensity;
        set => _post = _post with { LightShaftsIntensity = value };
    }

    /// <summary>The weight left after each sixteenth of a shaft (lower is shorter).</summary>
    [Export(Range = "0,1,0.001")]
    public float LightShaftsDecay
    {
        get => _post.LightShaftsDecay;
        set => _post = _post with { LightShaftsDecay = value };
    }

    /// <summary>The fraction of the way to the sun each shaft reaches.</summary>
    [Export(Range = "0,1,0.01")]
    public float LightShaftsDensity
    {
        get => _post.LightShaftsDensity;
        set => _post = _post with { LightShaftsDensity = value };
    }

    /// <summary>Taps per blur pass (4–64; two passes, so samples² per shaft).</summary>
    [Export(Range = "4,64,1")]
    public int LightShaftsSamples
    {
        get => _post.LightShaftsSamples;
        set => _post = _post with { LightShaftsSamples = value };
    }

    [ExportGroup("SSAO")]
    /// <summary>
    /// Screen-space ambient occlusion (ADR 0165; GTAO from the depth prepass): creases, contacts and the ground under
    /// objects lose ambient and reflected light. Only the tree's root world gets it; it turns the depth prepass on.
    /// </summary>
    [Export]
    public bool SsaoEnabled
    {
        get => _post.SsaoEnabled;
        set => _post = _post with { SsaoEnabled = value };
    }

    /// <summary>How far occluders reach, in metres (Godot's <c>ssao_radius</c>).</summary>
    [Export(Range = "0.01,16,0.01")]
    public float SsaoRadius
    {
        get => _post.SsaoRadius;
        set => _post = _post with { SsaoRadius = value };
    }

    /// <summary>Multiplies the occlusion (Godot's <c>ssao_intensity</c>; 1 with power 1 is the physical value).</summary>
    [Export(Range = "0,16,0.01")]
    public float SsaoIntensity
    {
        get => _post.SsaoIntensity;
        set => _post = _post with { SsaoIntensity = value };
    }

    /// <summary>Exponent of the occlusion: darker, with a sharper falloff (Godot's <c>ssao_power</c>).</summary>
    [Export(Range = "0,16,0.01")]
    public float SsaoPower
    {
        get => _post.SsaoPower;
        set => _post = _post with { SsaoPower = value };
    }

    /// <summary>Strength of the near-field detail term: small creases darken more (Godot's <c>ssao_detail</c>).</summary>
    [Export(Range = "0,5,0.01")]
    public float SsaoDetail
    {
        get => _post.SsaoDetail;
        set => _post = _post with { SsaoDetail = value };
    }

    /// <summary>Occluders lower than this fraction of 90° above the surface do not count (Godot's <c>ssao_horizon</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoHorizon
    {
        get => _post.SsaoHorizon;
        set => _post = _post with { SsaoHorizon = value };
    }

    /// <summary>How strictly the blur stops at depth edges (Godot's <c>ssao_sharpness</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoSharpness
    {
        get => _post.SsaoSharpness;
        set => _post = _post with { SsaoSharpness = value };
    }

    /// <summary>How much the occlusion also darkens direct light (Godot's <c>ssao_light_affect</c>; 0 = only ambient).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoLightAffect
    {
        get => _post.SsaoLightAffect;
        set => _post = _post with { SsaoLightAffect = value };
    }

    /// <summary>0: the darker of SSAO and the material's AO; 1: their product (Godot's <c>ssao_ao_channel_affect</c>).</summary>
    [Export(Range = "0,1,0.01")]
    public float SsaoAoChannelAffect
    {
        get => _post.SsaoAoChannelAffect;
        set => _post = _post with { SsaoAoChannelAffect = value };
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
