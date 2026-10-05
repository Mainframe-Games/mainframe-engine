using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// Pixel checks of the procedural sky and the scene grid: only those two are drawn, so every pixel away from a
/// grid line must be the sky colour the test recomputes on the CPU (ray → gradient → exposure → ACES → sRGB).
/// The camera stands inside the grid looking slightly down, so the frame shows sky above the horizon, the
/// horizon-to-ground gradient and the ground colour between grid lines, and grid lines pass beside and behind
/// the camera (their near-plane points project ~10⁵ pixels off-screen). The camera is offset from the integer
/// grid and yawed so no axis line lands on a pixel boundary.
/// </summary>
public sealed class SkyGridScene(HostOptions host) : RenderTestGame(host)
{
    public static readonly Vector3 CameraPosition = new(0.37f, 2f, 5.21f);
    public static readonly Vector3 CameraTarget = new(1.1f, 1.2f, 0f);

    /// <summary>Half-extent of the grid in lines (the <see cref="Grid3D"/> default).</summary>
    public const uint GridSize = 200;

    /// <summary>The sky parameters (the <see cref="Sky"/> resource defaults), shared with the test's reference.</summary>
    public static Sky SkySettings { get; } = new();

    private readonly PerspectiveCamera _camera = CreateCamera(4f / 3f);
    private SkyEnvironment _sky = null!;
    private SceneGrid3d _grid = null!;

    /// <summary>The camera the scene draws with, for an image of the given aspect (also used by the test).</summary>
    public static PerspectiveCamera CreateCamera(float aspectRatio)
    {
        var camera = new PerspectiveCamera { Position = CameraPosition, AspectRatio = aspectRatio };
        camera.LookAt(CameraTarget);
        return camera;
    }

    protected override void LoadScene()
    {
        var s = SkySettings;
        _sky = new SkyProcedural(Renderer)
        {
            SkyColor = s.SkyColor,
            HorizonColor = s.HorizonColor,
            GroundColor = s.GroundColor,
            SunDirection = s.SunDirection,
            SunColor = s.SunColor,
            SunIntensity = s.SunIntensity,
            SunAngularRadius = s.SunAngularRadius,
            HorizonSharpness = s.HorizonSharpness,
        };
        _grid = new SceneGrid3d(Renderer, GridSize);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void OnShadowPass(in GameTime gameTime)
    {
    }

    protected override void OnRenderMainPass(in GameTime gameTime)
    {
        _camera.AspectRatio = AspectRatio;
        _sky.Draw(_camera);
        _grid.Draw(_camera);
    }

    protected override void DisposeScene()
    {
        _grid.Dispose();
        _sky.Dispose();
    }
}
