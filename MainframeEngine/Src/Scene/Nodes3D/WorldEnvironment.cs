using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Sky and ambient light for its viewport's world (Godot's <c>WorldEnvironment</c>). The sky is described by a
/// <see cref="MainframeEngine.Sky"/> resource; the render server builds the matching <see cref="SkyEnvironment"/>
/// on first draw and rebuilds it when the sky's mode or images change. Only the first environment in a world is
/// used.
/// </summary>
public class WorldEnvironment : Node, IRenderResourceOwner
{
    private World3D? _world;
    private Vector3 _ambientColor = LightEnvironment.DefaultAmbientColor;
    private SkyEnvironment? _skyEnvironment;
    private RenderServer? _server;
    private SkyEnvironmentType _builtMode;
    private string? _builtPanorama;
    private string[]? _builtFaces;

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
