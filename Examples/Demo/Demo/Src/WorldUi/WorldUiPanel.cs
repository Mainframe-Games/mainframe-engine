using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>The World UI scene's controls: marker occlusion and distance scale, the FixedSize debug tags, a camera orbit.</summary>
public sealed class WorldUiPanel : UiDocument
{
    private readonly List<UiMarker3D> _plates = [];
    private readonly List<Label3D> _tags = [];
    private Camera3D? _camera;
    private Vector3 _cameraStart;
    private float _angle;

    public WorldUiPanel()
    {
        Source = "Assets/WorldUi/panel.rml";
        AutoFocus = false;
    }

    public bool Occlusion
    {
        get;
        set
        {
            field = value;
            foreach (var plate in _plates)
                plate.Occlusion = value ? MarkerOcclusion.Physics : MarkerOcclusion.None;
        }
    } = true;

    public bool ScaleWithDistance
    {
        get;
        set
        {
            field = value;
            foreach (var plate in _plates)
                plate.ScaleWithDistance = value;
        }
    } = true;

    public bool DebugTags
    {
        get;
        set
        {
            field = value;
            foreach (var tag in _tags)
                tag.Visible = value;
        }
    }

    /// <summary>Circles the camera around the street, so markers pass behind buildings and the waypoint clamps.</summary>
    public bool Orbit { get; set; }

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        foreach (var walker in scene.GetNode("Walkers").Children)
        {
            if (walker.GetNodeOrNull<UiMarker3D>("Nameplate") is { } plate)
                _plates.Add(plate);
            if (walker.GetNodeOrNull<Label3D>("DebugTag") is { } tag)
                _tags.Add(tag);
        }

        _camera = scene.GetNode<Camera3D>("Camera");
        _cameraStart = _camera.Position;
        CreateDataModel("worldui")
            .Bind("occlusion", this, static d => d.Occlusion, static (d, v) => d.Occlusion = v)
            .Bind("scale", this, static d => d.ScaleWithDistance, static (d, v) => d.ScaleWithDistance = v)
            .Bind("tags", this, static d => d.DebugTags, static (d, v) => d.DebugTags = v)
            .Bind("orbit", this, static d => d.Orbit, static (d, v) => d.Orbit = v);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!Orbit || _camera is null)
            return;
        _angle += gameTime.DeltaTime * 0.25f;
        var radius = new Vector2(_cameraStart.X, _cameraStart.Z).Length();
        _camera.Position = new Vector3(MathF.Sin(_angle) * radius, _cameraStart.Y, MathF.Cos(_angle) * radius);
        _camera.LookAt(new Vector3(0, 1.5f, 0));
    }

    protected override void OnExitTree()
    {
        _plates.Clear();
        _tags.Clear();
        _camera = null;
        base.OnExitTree();
    }
}
