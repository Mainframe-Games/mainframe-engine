using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Sky, ambient light, tonemap and glow for its viewport's world (Godot's <c>WorldEnvironment</c>). The sky is described by a
/// <see cref="MainframeEngine.Sky"/> resource; the render server builds the matching <see cref="SkyEnvironment"/>
/// on first draw and rebuilds it when the sky's mode or images change. Only the first environment in a world is
/// used.
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

    internal void DrawSky(RenderServer server, ICamera camera)
    {
        if (Sky is not { } sky)
            return;

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
        env.Draw(camera);
    }

    private void BuildSky(RenderServer server, Sky sky)
    {
        ReleaseSky();
        _skyEnvironment = sky.Mode switch
        {
            SkyEnvironmentType.Panoramic => new SkyPanoramic(server.Renderer, sky.Panorama),
            SkyEnvironmentType.Cubemap => new SkyCubemap(server.Renderer,
                sky.CubemapFaces ?? throw new InvalidOperationException("A cubemap sky needs 6 CubemapFaces.")),
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
}
