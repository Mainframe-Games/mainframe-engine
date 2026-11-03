namespace MainframeEngine;

/// <summary>How a <see cref="Grid3D"/> draws.</summary>
public enum GridStyle
{
    /// <summary>A fixed line list (<see cref="SceneGrid3d"/>): 1-pixel lines, <see cref="Grid3D.GridSize"/> each way.</summary>
    Lines,

    /// <summary>
    /// The editor grid: drawn per pixel on an endless plane at the node's height, anti-aliased lines
    /// <see cref="Grid3D.LineScale"/> × 1–2.5 pixels wide, minor and major levels that follow the zoom.
    /// </summary>
    Analytic,
}

/// <summary>
/// The 3D reference grid (wraps <see cref="SceneGrid3d"/>, or <see cref="SceneGridAnalytic"/> for
/// <see cref="GridStyle.Analytic"/>). A debug/editor visual (the design's <c>SceneGrid3D</c>, renamed so it does not differ
/// from the renderer class by case only): it casts no shadows and draws before other visuals
/// (<see cref="VisualInstance3D.RenderPriority"/> -100).
/// </summary>
[EditorIcon("grid-3x3")]
public sealed class Grid3D : VisualInstance3D
{
    private SceneGrid3d? _grid;
    private SceneGridAnalytic? _analytic;

    public Grid3D()
    {
        CastShadows = false;
        RenderPriority = -100;
    }

    /// <summary>Half-extent of the grid in lines (applied when the GPU objects are created; <see cref="GridStyle.Lines"/>).</summary>
    [Export(Range = "1,10000,1")]
    public int GridSize { get; set; } = 200;

    /// <summary>How the grid draws (applied when the GPU objects are created).</summary>
    [Export]
    public GridStyle Style { get; set; }

    /// <summary>
    /// <see cref="GridStyle.Analytic"/>: framebuffer pixels per point, so the lines keep their width on a HiDPI view (the
    /// editor sets its view scale). Not saved.
    /// </summary>
    public float LineScale { get; set; } = 1f;

    protected override void InitializeRenderResources(RenderServer server)
    {
        if (Style == GridStyle.Analytic)
            _analytic = new SceneGridAnalytic(server.Renderer);
        else
            _grid = new SceneGrid3d(server.Renderer, (uint)Math.Max(1, GridSize));
    }

    protected override void ReleaseRenderResources()
    {
        _grid?.Dispose();
        _grid = null;
        _analytic?.Dispose();
        _analytic = null;
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        _grid?.Draw(camera);
        if (_analytic is not null)
        {
            Sync(_analytic);
            _analytic.Draw(camera);
        }
    }

    internal override bool DrawsAfterPost => true;

    internal override void DrawAfterPost(ICamera camera, Silk.NET.Vulkan.RenderPass renderPass)
    {
        _grid?.DrawAfterPost(camera, renderPass);
        if (_analytic is not null)
        {
            Sync(_analytic);
            _analytic.DrawAfterPost(camera, renderPass);
        }
    }

    private void Sync(SceneGridAnalytic grid)
    {
        grid.PixelScale = LineScale;
        grid.PlaneY = GlobalPosition.Y;
    }
}
