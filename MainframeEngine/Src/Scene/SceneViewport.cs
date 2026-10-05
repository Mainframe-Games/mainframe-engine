using System.Numerics;
namespace MainframeEngine;

/// <summary>
/// Godot's <c>Viewport</c>: the tree root (and, later, sub-viewports for editor views). Holds a
/// <see cref="MainframeEngine.World3D"/> and a <see cref="MainframeEngine.World2D"/>, the active cameras and
/// the input-handled flag.
/// </summary>
/// <remarks>
/// Named <c>SceneViewport</c> rather than Godot's <c>Viewport</c> because the renderer uses
/// <c>Silk.NET.Vulkan.Viewport</c> unqualified throughout the <c>MainframeEngine</c> namespace (see the Godot-names ADR in memory/decisions).
/// </remarks>
[EditorIcon("device-desktop")]
public class SceneViewport : Node
{
    private readonly List<Camera3D> _cameras3D = [];
    private readonly List<Camera2D> _cameras2D = [];

    public SceneViewport()
        : this(isTreeRoot: false)
    {
    }

    internal SceneViewport(bool isTreeRoot)
    {
        IsTreeRoot = isTreeRoot;
        RootCanvas = new Canvas(this, null);
    }

    private readonly List<CanvasLayer> _canvasLayers = [];

    /// <summary>The viewport's own canvas (layer 0): canvas items not under a <see cref="CanvasLayer"/>.</summary>
    public Canvas RootCanvas { get; }

    /// <summary>
    /// Canvas space → viewport pixels for <see cref="RootCanvas"/> (Godot's <c>Viewport.canvas_transform</c>; the current
    /// <see cref="Camera2D"/> sets it).
    /// </summary>
    public Transform2D CanvasTransform { get; set; } = Transform2D.Identity;

    /// <summary>The canvas layers in this viewport (any order; the canvas server sorts them).</summary>
    public IReadOnlyList<CanvasLayer> CanvasLayers => _canvasLayers;

    // ── Size and content scale (Godot's Window content_scale_* for the root viewport) ──────────────────────────────

    /// <summary>How the root viewport's 2D content scales with the window (the root only; Godot's <c>content_scale_mode</c>).</summary>
    public ContentScaleMode ContentScaleMode { get; set; }

    public ContentScaleAspect ContentScaleAspect { get; set; } = ContentScaleAspect.Keep;

    /// <summary>The base size in canvas units (Godot's <c>display/window/size/viewport_width/height</c>).</summary>
    public Vector2 ContentScaleSize { get; set; }

    public float ContentScaleFactor { get; set; } = 1f;

    public ContentScaleStretch ContentScaleStretch { get; set; }

    /// <summary>The size of the viewport's target in pixels (the window framebuffer for the root).</summary>
    public Vector2 Size { get; private set; }

    /// <summary>The content scale result for <see cref="Size"/>.</summary>
    public ContentScaleResult ContentScaleResult { get; private set; }

    /// <summary>Canvas units → target pixels (Godot's stretch transform with the letterbox margin).</summary>
    public Transform2D StretchTransform { get; private set; } = Transform2D.Identity;

    /// <summary>The visible area in canvas units (Godot's <c>get_visible_rect</c>).</summary>
    public Rect2 GetVisibleRect() => new(Vector2.Zero, ContentScaleResult.VisibleSize);

    /// <summary>Sets the target size and recomputes the content scale (the engine calls this for the root each frame).</summary>
    public void SetSize(Vector2 pixels)
    {
        if (pixels == Size && _scaleKey == (ContentScaleMode, ContentScaleAspect, ContentScaleSize, ContentScaleFactor, ContentScaleStretch))
            return;
        Size = pixels;
        _scaleKey = (ContentScaleMode, ContentScaleAspect, ContentScaleSize, ContentScaleFactor, ContentScaleStretch);
        ContentScaleResult = ContentScale.Compute(pixels, ContentScaleMode, ContentScaleAspect, ContentScaleSize, ContentScaleFactor, ContentScaleStretch);
        StretchTransform = ContentScaleResult.StretchTransform;
    }

    private (ContentScaleMode, ContentScaleAspect, Vector2, float, ContentScaleStretch) _scaleKey;

    internal void AddCanvasLayer(CanvasLayer layer) => _canvasLayers.Add(layer);

    internal void RemoveCanvasLayer(CanvasLayer layer) => _canvasLayers.Remove(layer);

    /// <summary>True for <see cref="SceneTree.Root"/>.</summary>
    public bool IsTreeRoot { get; }

    /// <summary>The 3D world (render scenario) of this viewport.</summary>
    public World3D World3D { get; } = new();

    /// <summary>The 2D world of this viewport.</summary>
    public World2D World2D { get; } = new();

    /// <summary>
    /// Debug line batch drawn after this viewport's visuals and cleared every frame (physics shapes, gizmos). 2D
    /// producers draw in the z = 0 plane.
    /// </summary>
    public DebugLines DebugLines { get; } = new();

    /// <summary>
    /// Like <see cref="DebugLines"/> but drawn after them without depth testing, so the lines stay visible through
    /// geometry (editor gizmos, selection handles). Cleared every frame.
    /// </summary>
    public DebugLines OverlayLines { get; } = new();

    /// <summary>
    /// A camera that renders this viewport instead of <see cref="ActiveCamera3D"/> (tools: the editor's own camera,
    /// which must not touch the scene's cameras or their <c>Current</c> flags). A <see cref="PerspectiveCamera"/>
    /// gets the view's aspect ratio each frame. Null (default) uses the scene's cameras.
    /// </summary>
    public ICamera? CameraOverride { get; set; }

    /// <summary>
    /// The camera the 3D world is rendered with: the last camera made <see cref="Camera3D.Current"/>, or the
    /// first one to enter the viewport.
    /// </summary>
    public Camera3D? ActiveCamera3D { get; private set; }

    /// <summary>The active 2D camera (used when there is no <see cref="ActiveCamera3D"/>).</summary>
    public Camera2D? ActiveCamera2D { get; private set; }

    /// <summary>True once a node handled the event being routed by <see cref="SceneTree.PushInput"/>.</summary>
    public bool IsInputHandled { get; private set; }

    /// <summary>Stops the current input event from reaching further nodes.</summary>
    public void SetInputAsHandled() => IsInputHandled = true;

    internal void BeginInput() => IsInputHandled = false;

    internal void AddCamera(Camera3D camera)
    {
        _cameras3D.Add(camera);
        if (camera.Current)
            MakeCurrent(camera);
        else
            ActiveCamera3D ??= camera;
    }

    internal void RemoveCamera(Camera3D camera)
    {
        _cameras3D.Remove(camera);
        if (ReferenceEquals(ActiveCamera3D, camera))
            ActiveCamera3D = _cameras3D.Count > 0 ? FindCurrent(_cameras3D) : null;
    }

    /// <summary>Only one camera per viewport is current: the others lose the flag.</summary>
    internal void MakeCurrent(Camera3D camera)
    {
        foreach (var other in _cameras3D)
            if (!ReferenceEquals(other, camera))
                other.ClearCurrentFlag();
        ActiveCamera3D = camera;
    }

    internal void ClearCurrent(Camera3D camera)
    {
        if (ReferenceEquals(ActiveCamera3D, camera))
            ActiveCamera3D = FindCurrent(_cameras3D, except: camera) ?? camera;
    }

    // 2D cameras follow Godot: a camera is active only once made current (Camera2D makes itself current on entering a
    // viewport without one when Enabled); when the current one leaves, the next enabled camera takes over.
    internal void AddCamera(Camera2D camera)
    {
        _cameras2D.Add(camera);
        if (camera.Current)
            MakeCurrent(camera);
    }

    internal void RemoveCamera(Camera2D camera)
    {
        _cameras2D.Remove(camera);
        if (!ReferenceEquals(ActiveCamera2D, camera))
            return;
        ActiveCamera2D = null;
        foreach (var next in _cameras2D)
            if (next.Enabled)
            {
                next.MakeCurrent();
                break;
            }
    }

    internal void MakeCurrent(Camera2D camera)
    {
        foreach (var other in _cameras2D)
            if (!ReferenceEquals(other, camera))
                other.ClearCurrentFlag();
        ActiveCamera2D = camera;
    }

    private static T? FindCurrent<T>(List<T> cameras, T? except = null) where T : Node, ICurrentCamera
    {
        T? fallback = null;
        foreach (var camera in cameras)
        {
            if (ReferenceEquals(camera, except))
                continue;
            if (camera.Current)
                return camera;
            fallback ??= camera;
        }

        return fallback;
    }
}

/// <summary>Cameras that can be made current in a viewport.</summary>
internal interface ICurrentCamera
{
    bool Current { get; }
}
