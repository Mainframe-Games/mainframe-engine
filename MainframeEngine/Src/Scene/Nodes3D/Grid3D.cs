namespace MainframeEngine;

/// <summary>
/// The 3D reference grid (wraps <see cref="SceneGrid3d"/>). A debug/editor visual (the design's
/// <c>SceneGrid3D</c>, renamed so it does not differ from the renderer class by case only): it casts no shadows
/// and draws before other visuals (<see cref="VisualInstance3D.RenderPriority"/> -100).
/// </summary>
public sealed class Grid3D : VisualInstance3D
{
    private SceneGrid3d? _grid;

    public Grid3D()
    {
        CastShadows = false;
        RenderPriority = -100;
    }

    /// <summary>Half-extent of the grid in lines (applied when the GPU objects are created).</summary>
    [Export(Range = "1,10000,1")]
    public int GridSize { get; set; } = 200;

    protected override void InitializeRenderResources(RenderServer server) =>
        _grid = new SceneGrid3d(server.Renderer, (uint)Math.Max(1, GridSize));

    protected override void ReleaseRenderResources()
    {
        _grid?.Dispose();
        _grid = null;
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment) => _grid?.Draw(camera);
}
