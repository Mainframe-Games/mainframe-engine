using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A perspective camera node: looks along its <c>-Z</c> axis from its global position. The viewport renders
/// with its active camera (<see cref="SceneViewport.ActiveCamera3D"/>): the one marked <see cref="Current"/>,
/// or the first to enter. Wraps a <see cref="PerspectiveCamera"/> that the render server syncs each frame.
/// </summary>
[EditorIcon("video")]
public class Camera3D : Node3D, ICurrentCamera
{
    private readonly PerspectiveCamera _camera = new();
    private bool _current;

    /// <summary>Makes this the viewport's active camera (only one camera per viewport is current).</summary>
    [Export]
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            if (GetViewport() is not { } viewport)
                return;
            if (value)
                viewport.MakeCurrent(this);
            else
                viewport.ClearCurrent(this);
        }
    }

    /// <summary>Vertical field of view in degrees.</summary>
    [Export(Range = "1,179,0.1")]
    public float Fov
    {
        get => _camera.FieldOfView;
        set => _camera.FieldOfView = value;
    }

    [Export(Range = "0.001,10,0.001")]
    public float Near
    {
        get => _camera.Near;
        set => _camera.Near = value;
    }

    [Export(Range = "0.01,100000,0.01")]
    public float Far
    {
        get => _camera.Far;
        set => _camera.Far = value;
    }

    /// <summary>
    /// This camera's lens (Godot's <c>Camera3D.attributes</c>, ADR 0168): depth of field and film effects, replacing the
    /// world's <see cref="WorldEnvironment.CameraAttributes"/> while this is the root view's current camera.
    /// </summary>
    [Export]
    public CameraAttributesPractical? Attributes { get; set; }

    /// <summary>True while this is the viewport's active camera.</summary>
    public bool IsActive => ReferenceEquals(GetViewport()?.ActiveCamera3D, this);

    /// <summary>The camera math as last synced by the renderer (position/forward/up/aspect).</summary>
    public ICamera RenderCamera => _camera;

    /// <summary>Makes this the active camera of its viewport.</summary>
    public void MakeCurrent() => Current = true;

    internal void ClearCurrentFlag() => _current = false;

    /// <summary>Copies the global transform and <paramref name="aspectRatio"/> into the wrapped camera.</summary>
    public ICamera SyncRenderCamera(float aspectRatio)
    {
        var global = GlobalTransform;
        _camera.Position = global.Origin;
        _camera.Forward = Vector3.Normalize(-global.Basis.Z);
        _camera.Up = Vector3.Normalize(global.Basis.Y);
        _camera.AspectRatio = aspectRatio;
        return _camera;
    }

    /// <summary>
    /// The world-space ray through <paramref name="pixel"/> (top-left origin, framebuffer pixels) of a viewport of
    /// <paramref name="viewportSize"/> seen by <paramref name="camera"/> (Godot's <c>project_ray_origin/normal</c>).
    /// </summary>
    public static (Vector3 Origin, Vector3 Direction) ProjectRay(ICamera camera, Vector2 pixel, Vector2 viewportSize)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!(viewportSize.X > 0f && viewportSize.Y > 0f))
            return (camera.Position, camera.Forward); // no viewport area (0x0): no pixel to look through, not NaN
        var ndc = new Vector2(pixel.X / viewportSize.X * 2f - 1f, 1f - pixel.Y / viewportSize.Y * 2f);
        if (!Matrix4x4.Invert(camera.ViewMatrix * camera.ProjectionMatrix, out var inverse))
            return (camera.Position, camera.Forward);
        // z = 0.5 is inside the clip volume for both 0..1 and -1..1 depth conventions.
        var far = Vector4.Transform(new Vector4(ndc, 0.5f, 1f), inverse);
        var point = new Vector3(far.X, far.Y, far.Z) / far.W;
        return (camera.Position, Vector3.Normalize(point - camera.Position));
    }

    /// <summary>Origin of the ray through <paramref name="pixel"/> of this camera's viewport (framebuffer pixels).</summary>
    public Vector3 ProjectRayOrigin(Vector2 pixel) => RayThrough(pixel).Origin;

    /// <summary>Direction of the ray through <paramref name="pixel"/> of this camera's viewport (framebuffer pixels).</summary>
    public Vector3 ProjectRayNormal(Vector2 pixel) => RayThrough(pixel).Direction;

    private (Vector3 Origin, Vector3 Direction) RayThrough(Vector2 pixel)
    {
        var size = GetViewport()?.Size ?? Vector2.One;
        return ProjectRay(SyncRenderCamera(size.X / MathF.Max(size.Y, 1f)), pixel, size);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        GetViewport()?.AddCamera(this);
    }

    protected override void OnExitTree()
    {
        GetViewport()?.RemoveCamera(this);
        base.OnExitTree();
    }
}
