using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Paths 3D scene's controls: platform speed, pause, riding the camera rail, and a jump button.</summary>
public sealed class Paths3DPanel : UiDocument
{
    private readonly List<(PathFollow3D Follow, float Speed)> _followers = [];
    private Turntable3D? _table;
    private PathGuard3D? _guard;
    private float _guardSpeed;
    private Camera3D? _overview;
    private Camera3D? _railCamera;
    private PathRider3D? _rider;
    private float _scale = 1f;
    private bool _paused;
    private bool _rideRail;

    public Paths3DPanel()
    {
        Source = "Assets/Paths3D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>Multiplies the platforms', the rail's and the guard's speeds.</summary>
    public float SpeedScale
    {
        get => _scale;
        set
        {
            _scale = value;
            Apply();
        }
    }

    /// <summary>Stops every platform, the rail and the guard.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Apply();
        }
    }

    /// <summary>Views the yard from the camera on the rail instead of the overview.</summary>
    public bool RideRail
    {
        get => _rideRail;
        set
        {
            _rideRail = value;
            if (_railCamera is not null && _overview is not null)
            {
                _railCamera.Current = value;
                _overview.Current = !value;
            }
        }
    }

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        foreach (var path in new[] { "LiftPath", "FerryPath", "Rail" })
            if (scene.GetNode(path).GetNodeOrNull<PathFollow3D>("Follow") is { } follow)
                _followers.Add((follow, follow.Speed));
        _table = scene.GetNode<Turntable3D>("Turntable");
        _guard = scene.GetNode<PathGuard3D>("Guard");
        _guardSpeed = _guard.Speed;
        _overview = scene.GetNode<Camera3D>("Camera");
        _railCamera = scene.GetNode<Camera3D>("Rail/Follow/Camera");
        _rider = scene.GetNode<PathRider3D>("Rider");
        CreateDataModel("paths3d")
            .Bind("speed", this, static d => d.SpeedScale, static (d, v) => d.SpeedScale = v)
            .Bind("paused", this, static d => d.Paused, static (d, v) => d.Paused = v)
            .Bind("rail", this, static d => d.RideRail, static (d, v) => d.RideRail = v)
            .Event("jump", Jump);
    }

    private void Jump() => _rider?.Jump();

    private void Apply()
    {
        var scale = _paused ? 0f : _scale;
        foreach (var (follow, speed) in _followers)
            follow.Speed = speed * scale;
        if (_table is not null)
            _table.SpeedScale = scale;
        if (_guard is not null)
            _guard.Speed = _guardSpeed * scale;
    }

    protected override void OnExitTree()
    {
        _followers.Clear();
        _table = null;
        _guard = null;
        _overview = _railCamera = null;
        _rider = null;
        base.OnExitTree();
    }
}
