using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// An orthographic camera node for 2D scenes: centred on its global position, looking along <c>+Z</c> at the z = 0
/// plane from <see cref="Distance"/> units behind it, so +Y is down on screen (2D is Y-down, as in Godot). 3D visuals
/// it draws are seen from behind (use double-sided materials for them). Used by the render server when the viewport has no
/// <see cref="Camera3D"/>. Wraps an <see cref="OrthographicCamera"/>.
/// </summary>
[EditorIcon("camera")]
public class Camera2D : Node2D, ICurrentCamera
{
    private readonly OrthographicCamera _camera = new() { Forward = Vector3.UnitZ, Up = -Vector3.UnitY };
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

    /// <summary>Distance of the camera behind the z = 0 plane (content must lie within near/far of it).</summary>
    [Export]
    public float Distance { get; set; } = 500f;

    public ICamera RenderCamera => _camera;

    public void MakeCurrent() => Current = true;

    internal void ClearCurrentFlag() => _current = false;

    /// <summary>Copies the global position and viewport size into the wrapped camera.</summary>
    public ICamera SyncRenderCamera(Vector2 viewportSize)
    {
        var position = GlobalPosition;
        _camera.Position = new Vector3(position, -Distance);
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
