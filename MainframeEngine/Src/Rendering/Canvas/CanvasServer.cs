using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The 2D canvas server (Godot's <c>RendererCanvasCull</c> + the viewport's canvas pass; docs/design/canvas.md, ADR 0111).
/// Each frame, after the tree's process step: runs the queued <see cref="CanvasItem"/> draws, then culls and orders the
/// root viewport's canvases — the root canvas (layer 0) and every visible <see cref="CanvasLayer"/>, by layer — into one
/// <see cref="CanvasFrame"/> in target pixels. The renderer (<see cref="VulkanCanvasRenderer"/>) draws that frame into an
/// RGBA8 layer in gamma space, as Godot does with <c>hdr_2d</c> off, and composites it after the tonemap, below the game
/// UI. Headless (no renderer) the frame is still built, so tests and tools can inspect it.
/// </summary>
public sealed class CanvasServer : IFrameServer
{
    /// <summary>Godot's default clear colour (<c>rendering/environment/defaults/default_clear_color</c>).</summary>
    public static readonly Vector4 GodotDefaultClearColor = new(0.3f, 0.3f, 0.3f, 1f);

    private readonly SceneTree _tree;
    private readonly Func<Vector2> _targetSize;
    private readonly CanvasCuller _culler = new();
    private readonly List<CulledCanvasItem> _culled = new(1024);
    private readonly List<(Canvas Canvas, int Layer, int Order)> _canvases = new(8);
    private double _time;
    private bool _disposed;

    /// <param name="tree">The tree whose root viewport is drawn.</param>
    /// <param name="targetSize">The size of the target in pixels (the framebuffer), read each frame.</param>
    /// <param name="renderer">The device renderer, or null headless.</param>
    public CanvasServer(SceneTree tree, Func<Vector2> targetSize, IRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(targetSize);
        _tree = tree;
        _targetSize = targetSize;
        if (renderer is IVulkanContext vk)
            Renderer = new VulkanCanvasRenderer(vk, this);
    }

    /// <summary>The frame built by the last <see cref="Process"/>.</summary>
    public CanvasFrame Frame { get; } = new();

    /// <summary>The device renderer (null headless).</summary>
    public VulkanCanvasRenderer? Renderer { get; }

    /// <summary>
    /// Opaque colour the canvas is cleared to (a 2D game: Godot's viewport clear colour, which also hides the 3D
    /// scene), or null (default) to draw the canvas over the 3D scene with a transparent background.
    /// </summary>
    public Vector4? ClearColor { get; set; }

    /// <summary>Godot's shader <c>TIME</c>: seconds since start, wrapping every <see cref="TimeRolloverSeconds"/>.</summary>
    public float Time => (float)_time;

    /// <summary>Godot's <c>rendering/limits/time/time_rollover_secs</c>.</summary>
    public double TimeRolloverSeconds { get; set; } = 3600.0;

    /// <summary>Canvas item draws run by the last <see cref="Process"/>.</summary>
    public int LastRedraws { get; private set; }

    public void Process(in GameTime gameTime)
    {
        if (_disposed)
            return;
        _time = (_time + gameTime.DeltaTime) % TimeRolloverSeconds;
        LastRedraws = _tree.PendingCanvasRedraws;
        _tree.FlushCanvasRedraws();
        var size = _targetSize();
        _tree.Root.SetSize(size);
        BuildFrame(_tree.Root, size);
        Renderer?.PrepareTextures(Frame);
    }

    // The 2D sub-viewports (Disable3D) that draw this frame, each into its own target. Views register on entering the
    // tree (parents before children), so walking them backwards draws a nested view before the view that samples it.
    private void AppendSubViewports(CanvasFrame frame)
    {
        if (_tree.Servers.Render is not { } render)
            return;
        var subs = render.SubViewports;
        for (var i = subs.Count - 1; i >= 0; i--)
        {
            var sub = subs[i];
            if (!sub.Disable3D || !sub.IsInsideTree || sub.UpdateMode == SubViewportUpdateMode.Disabled || sub.Width <= 0 || sub.Height <= 0)
                continue;
            var size = new Vector2(sub.Width, sub.Height);
            sub.SetSize(size);
            frame.BeginPass(sub, size, sub.TransparentBg ? Vector4.Zero : ClearColor ?? GodotDefaultClearColor);
            AppendViewport(frame, sub, size);
            frame.EndPass();
            if (sub.UpdateMode == SubViewportUpdateMode.Once)
                sub.UpdateMode = SubViewportUpdateMode.Disabled; // Godot's UPDATE_ONCE: draw on the next frame, then keep it
        }
    }

    /// <summary>Culls and batches every canvas of <paramref name="viewport"/> into <see cref="Frame"/>.</summary>
    public void BuildFrame(SceneViewport viewport, Vector2 targetSize)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var frame = Frame;
        frame.Clear();
        frame.TargetSize = targetSize;
        frame.ClearColor = ClearColor;
        AppendSubViewports(frame);
        frame.BeginPass(null, targetSize, ClearColor);
        AppendViewport(frame, viewport, targetSize);
        frame.EndPass();
    }

    private void AppendViewport(CanvasFrame frame, SceneViewport viewport, Vector2 targetSize)
    {
        // The drawn area: the whole target, or the letterboxed rect of the content scale (canvas units × stretch).
        var scale = viewport.ContentScaleResult;
        var clip = scale.RenderSize.X > 0 ? new Rect2(scale.Margin, scale.RenderSize) : new Rect2(Vector2.Zero, targetSize);
        var stretch = viewport.StretchTransform;

        // Canvases by layer; the root canvas is layer 0 and draws before layers of equal number.
        _canvases.Clear();
        _canvases.Add((viewport.RootCanvas, 0, -1));
        var layers = viewport.CanvasLayers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.Visible)
                _canvases.Add((layer.Canvas, layer.Layer, layer.GetIndex()));
        }

        _canvases.Sort(CanvasOrder.Instance);

        for (var i = 0; i < _canvases.Count; i++)
        {
            var canvas = _canvases[i].Canvas;
            if (canvas.RootCount == 0)
                continue;
            var transform = stretch * canvas.Transform;
            _culled.Clear();
            _culler.Cull(canvas, transform, clip, _culled);
            if (_culled.Count == 0)
                continue;
            frame.Append(_culled, canvas.Modulate, transform, transform.AffineInverse());
        }
    }

    // By layer, then tree order (the root canvas, order −1, first among layer 0). A singleton: no per-frame allocation.
    private sealed class CanvasOrder : IComparer<(Canvas Canvas, int Layer, int Order)>
    {
        public static readonly CanvasOrder Instance = new();

        public int Compare((Canvas Canvas, int Layer, int Order) a, (Canvas Canvas, int Layer, int Order) b) =>
            a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Order.CompareTo(b.Order);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Renderer?.Dispose();
    }
}
