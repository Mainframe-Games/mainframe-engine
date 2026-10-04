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
