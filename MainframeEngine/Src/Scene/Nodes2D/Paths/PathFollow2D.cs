using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Places itself, and so its children, on its parent <see cref="Path2D"/>'s curve at <see cref="Progress"/> pixels
/// (Godot's <c>PathFollow2D</c>, G13). With <see cref="Rotates"/>, <c>+X</c> points along the curve. Extensions, as
/// <see cref="PathFollow3D"/>: <see cref="Speed"/> (move without code), <see cref="PingPong"/>,
/// <see cref="ProcessCallback"/> and the <see cref="Looped"/> and <see cref="EndReached"/> signals. The scale is kept;
/// placing allocates nothing.
/// </summary>
[EditorIcon("map-pin")]
public class PathFollow2D : Node2D
{
    private float _progress;
    private float _hOffset;
    private float _vOffset;
    private bool _rotates = true;
    private bool _cubic = true;
    private bool _loop = true;
    private float _speed;
    private PathProcessCallback _processCallback;
    private float _direction = 1f;
    private bool _atEnd;
    private bool _warned;

    /// <summary>Pixels along the curve. Wraps when <see cref="Loop"/> (or the curve is closed), else clamps.</summary>
    [Export(Range = "0,1000000,0.01")]
    public float Progress
    {
        get => _progress;
        set
        {
            _progress = Constrain(value);
            UpdatePlacement();
        }
    }

    /// <summary><see cref="Progress"/> as a fraction of the curve's length (0 on an empty curve).</summary>
    public float ProgressRatio
    {
        get => CurveLength is var length and > 0 ? _progress / length : 0f;
        set => Progress = value * CurveLength;
    }

    /// <summary>Offset along the curve (Godot's <c>h_offset</c>): along the follower's local X, or the path's X without <see cref="Rotates"/>.</summary>
    [Export]
    public float HOffset
    {
        get => _hOffset;
        set
        {
            _hOffset = value;
            UpdatePlacement();
        }
    }

    /// <summary>Offset across the curve (Godot's <c>v_offset</c>): along the follower's local Y, or the path's Y without <see cref="Rotates"/>.</summary>
    [Export]
    public float VOffset
    {
        get => _vOffset;
        set
        {
            _vOffset = value;
            UpdatePlacement();
        }
    }

    /// <summary>Turn with the curve (<c>+X</c> along it); off keeps the rotation and moves only the position.</summary>
    [Export]
    public bool Rotates
    {
        get => _rotates;
        set
        {
            _rotates = value;
            UpdatePlacement();
        }
    }

    /// <summary>Catmull-Rom through the baked points instead of straight lines between them (Godot's <c>cubic_interp</c>).</summary>
    [Export]
    public bool CubicInterp
    {
        get => _cubic;
        set
        {
            _cubic = value;
            UpdatePlacement();
        }
    }

    /// <summary>Wrap <see cref="Progress"/> past the ends (Godot's default); off clamps it. A closed curve always wraps.</summary>
    [Export]
    public bool Loop
    {
        get => _loop;
        set
        {
            _loop = value;
            Progress = _progress;
        }
    }

    /// <summary>Pixels per second to advance by itself (extension); 0 moves only when <see cref="Progress"/> is set.</summary>
    [Export(Range = "-100000,100000,0.1")]
    public float Speed
    {
        get => _speed;
        set
        {
            _speed = value;
            UpdateCallbacks();
        }
    }

    /// <summary>With <see cref="Speed"/> and without <see cref="Loop"/>: turn back at the ends instead of stopping (extension).</summary>
    [Export]
    public bool PingPong { get; set; }

    /// <summary>Which callback advances <see cref="Speed"/>: <see cref="PathProcessCallback.Physics"/> for bodies.</summary>
    [Export]
    public PathProcessCallback ProcessCallback
    {
        get => _processCallback;
        set
        {
            _processCallback = value;
            UpdateCallbacks();
        }
    }

    /// <summary>Speed movement wrapped past the end (or the start).</summary>
    [Signal] public event Action? Looped;

    /// <summary>Speed movement stopped at an end, or turned there with <see cref="PingPong"/>.</summary>
    [Signal] public event Action? EndReached;

    private Curve2D? PathCurve => Parent is Path2D path ? path.Curve : null;

    private float CurveLength => PathCurve?.GetBakedLength() ?? 0f;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (Parent is not Path2D && !_warned)
        {
            _warned = true;
            Log.Warning($"[PathFollow2D] '{Name}' is not a child of a Path2D; it does not follow a path.");
        }

        UpdateCallbacks();
        Progress = _progress;
    }

    protected override void OnProcess(in GameTime gameTime) => Advance(gameTime.DeltaTime);

    protected override void OnPhysicsProcess(float delta) => Advance(delta);

    /// <summary>Re-places the follower on its path (its path calls this when the curve changes).</summary>
    internal void UpdatePlacement()
    {
        if (PathCurve is not { } curve || curve.GetBakedLength() <= 0)
            return;

        var origin = curve.SampleBaked(_progress, _cubic);
        if (!_rotates)
        {
            // Godot: without rotation the offsets are along the path's own X and Y.
            Position = origin + new Vector2(_hOffset, _vOffset);
            return;
        }

        var x = curve.SampleBakedTangent(_progress);
        var y = new Vector2(-x.Y, x.X);
        Position = origin + x * _hOffset + y * _vOffset;
        Rotation = MathF.Atan2(x.Y, x.X);
    }

    private void Advance(float delta)
    {
        if (_speed == 0 || PathCurve is not { } curve)
            return;
        var length = curve.GetBakedLength();
        if (length <= 0)
            return;

        var step = PathMotion.Step(_progress, _speed, delta, length, _loop || curve.Closed, PingPong, ref _direction, ref _atEnd, out var next);
        if (step == PathStepEvent.Looped)
            Looped?.Invoke();
        else if (step == PathStepEvent.EndReached)
            EndReached?.Invoke();
        Progress = next;
    }

    private float Constrain(float value)
    {
        var curve = PathCurve;
        return PathMotion.Constrain(value, curve?.GetBakedLength() ?? 0f, _loop || curve is { Closed: true });
    }

    private void UpdateCallbacks()
    {
        var moving = _speed != 0;
        SetProcess(moving && _processCallback == PathProcessCallback.Idle);
        SetPhysicsProcess(moving && _processCallback == PathProcessCallback.Physics);
    }
}
