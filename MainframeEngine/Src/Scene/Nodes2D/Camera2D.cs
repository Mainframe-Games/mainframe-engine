using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// An orthographic camera node for 2D scenes: centred on its global position, looking down <c>-Z</c> from
/// <see cref="Distance"/> units in front of the z = 0 plane. Used by the render server when the viewport has no
/// <see cref="Camera3D"/>. Wraps an <see cref="OrthographicCamera"/>.
/// </summary>
public class Camera2D : Node2D, ICurrentCamera
{
    private readonly OrthographicCamera _camera = new();
    private bool _current;

    [Export]
    public bool Current
    {
        get => _current;
        set
        {
            if (_current == value)
                return;
            _current = value;
            if (value && GetViewport() is { } viewport)
                viewport.MakeCurrent(this);
        }
    }

    /// <summary>World units per pixel (1 = one unit per framebuffer pixel; smaller zooms in).</summary>
    [Export(Range = "0.001,10,0.001")]
    public float Zoom
    {
        get => _camera.Zoom;
        set => _camera.Zoom = value;
    }

    /// <summary>Distance of the camera from the z = 0 plane (content must lie within near/far of it).</summary>
    [Export]
    public float Distance { get; set; } = 500f;

    public ICamera RenderCamera => _camera;

    public void MakeCurrent() => Current = true;

    internal void ClearCurrentFlag() => _current = false;

    /// <summary>Copies the global position and viewport size into the wrapped camera.</summary>
    public ICamera SyncRenderCamera(Vector2 viewportSize)
    {
        var position = GlobalPosition;
        _camera.Position = new Vector3(position, Distance);
        _camera.Size = viewportSize;
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
