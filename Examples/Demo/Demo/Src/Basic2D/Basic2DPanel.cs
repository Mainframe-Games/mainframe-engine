using System.Numerics;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Basic 2D scene's controls: camera zoom and pausing the animation.</summary>
public sealed class Basic2DPanel : UiDocument
{
    private Camera2D? _camera;
    private Orbit2D? _orbit;
    private SunRays2D? _rays;

    public Basic2DPanel()
    {
        Source = "Content/Basic2D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>The scene camera's zoom (both axes); the panel's zoom slider is bound to it.</summary>
    public float Zoom
    {
        get => _camera?.Zoom.X ?? 1f;
        set { if (_camera is { } camera) camera.Zoom = new Vector2(value); }
    }

    /// <summary>Whether the orbit and the sun's rays are paused; the panel's checkbox is bound to it.</summary>
    public bool Paused
    {
        get => _orbit?.Paused ?? false;
        set
        {
            if (_orbit is { } orbit) orbit.Paused = value;
            if (_rays is { } rays) rays.Paused = value;
        }
    }

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _camera = scene.GetNode<Camera2D>("Camera");
        _orbit = scene.GetNode<Orbit2D>("Orbit");
        _rays = scene.GetNode<SunRays2D>("Sun/Rays");
        CreateDataModel("basic2d")
            .Bind("zoom", this, static d => d.Zoom, static (d, v) => d.Zoom = v)
            .Bind("paused", this, static d => d.Paused, static (d, v) => d.Paused = v);
    }

    protected override void OnExitTree()
    {
        _camera = null;
        _orbit = null;
        _rays = null;
        base.OnExitTree();
    }
}
