using System.Numerics;

namespace MainframeEngine;

/// <summary>How a <see cref="PathFollow3D"/> turns with its path (Godot's <c>RotationMode</c>).</summary>
public enum PathRotationMode : byte
{
    /// <summary>Position only; the rotation is left alone.</summary>
    None,

    /// <summary>Yaw only: <c>-Z</c> along the tangent projected on the XZ plane.</summary>
    Y,

    /// <summary>Yaw and pitch: <c>-Z</c> along the tangent, <c>+Y</c> toward world up, no roll.</summary>
    XY,

    /// <summary><see cref="XY"/> plus the curve's tilt as roll (when <see cref="PathFollow3D.TiltEnabled"/>).</summary>
    XYZ,

    /// <summary>
    /// The curve's baked rotation-minimising frame plus tilt: smooth through loops and vertical climbs. Needs
    /// <see cref="Curve3D.UpVectorEnabled"/>, else behaves as <see cref="XYZ"/>.
    /// </summary>
    Oriented,
}

/// <summary>Which callback advances a <see cref="PathFollow3D"/> with a <see cref="PathFollow3D.Speed"/>.</summary>
public enum PathProcessCallback : byte
{
    /// <summary><see cref="Node.OnProcess"/>, once per frame (visuals).</summary>
    Idle,

    /// <summary><see cref="Node.OnPhysicsProcess"/>, on the fixed tick (bodies under the follower).</summary>
    Physics,
}

/// <summary>
/// Places itself, and so its children, on its parent <see cref="Path3D"/>'s curve at <see cref="Progress"/> metres
/// (Godot's <c>PathFollow3D</c>, ADR 0215). Forward is <c>-Z</c>, the engine's forward. Extensions: <see cref="Speed"/>
/// (move without code), <see cref="PingPong"/>, <see cref="ProcessCallback"/> and the <see cref="Looped"/> and
/// <see cref="EndReached"/> signals. The scale is kept; placing allocates nothing.
/// </summary>
[EditorIcon("map-pin")]
public class PathFollow3D : Node3D
{
    private float _progress;
    private float _hOffset;
    private float _vOffset;
    private PathRotationMode _rotationMode = PathRotationMode.XYZ;
    private bool _cubic = true;
    private bool _loop = true;
    private bool _tiltEnabled = true;
    private float _speed;
    private PathProcessCallback _processCallback;
    private float _direction = 1f;
    private bool _atEnd;
    private bool _warned;

    /// <summary>Metres along the curve. Wraps when <see cref="Loop"/> (or the curve is closed), else clamps.</summary>
    [Export(Range = "0,100000,0.01")]
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

    /// <summary>Offset along the follower's local X (sideways).</summary>
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

    /// <summary>Offset along the follower's local Y (up).</summary>
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

    [Export]
    public PathRotationMode RotationMode
    {
        get => _rotationMode;
        set
        {
            _rotationMode = value;
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

    /// <summary>Roll with the curve's tilt in <see cref="PathRotationMode.XYZ"/> and <see cref="PathRotationMode.Oriented"/>.</summary>
    [Export]
    public bool TiltEnabled
    {
        get => _tiltEnabled;
        set
        {
            _tiltEnabled = value;
            UpdatePlacement();
        }
    }

    /// <summary>Metres per second to advance by itself (extension); 0 moves only when <see cref="Progress"/> is set.</summary>
    [Export(Range = "-1000,1000,0.01")]
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

    private Curve3D? PathCurve => Parent is Path3D path ? path.Curve : null;

    private float CurveLength => PathCurve?.GetBakedLength() ?? 0f;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (Parent is not Path3D && !_warned)
        {
            _warned = true;
            Log.Warning($"[PathFollow3D] '{Name}' is not a child of a Path3D; it does not follow a path.");
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

        var offset = _progress;
        var origin = curve.SampleBaked(offset, _cubic);
        var tangent = curve.SampleBakedTangent(offset);
        var tilt = _tiltEnabled ? curve.SampleBakedTilt(offset) : 0f;
        Basis basis;
        switch (_rotationMode)
        {
            case PathRotationMode.Y:
                var flat = new Vector3(tangent.X, 0, tangent.Z);
                basis = flat.LengthSquared() > 1e-12f ? Transform3D.BasisLookingAlong(flat, Vector3.UnitY) : Basis.Identity;
                break;
            case PathRotationMode.Oriented when curve.UpVectorEnabled:
                var z = -tangent;
                var y = curve.SampleBakedUpVector(offset);
                basis = Roll(new Basis(Vector3.Cross(y, z), y, z), tangent, tilt);
                break;
            case PathRotationMode.XYZ or PathRotationMode.Oriented:
                basis = Roll(Transform3D.BasisLookingAlong(tangent, Vector3.UnitY), tangent, tilt);
                break;
            default: // None (offsets use the XY frame) and XY
                basis = Transform3D.BasisLookingAlong(tangent, Vector3.UnitY);
                break;
        }

        Position = origin + basis.X * _hOffset + basis.Y * _vOffset;
        if (_rotationMode != PathRotationMode.None)
            Rotation = basis.GetRotation();
    }

    private static Basis Roll(Basis basis, Vector3 axis, float angle)
    {
        if (angle == 0)
            return basis;
        var roll = Quaternion.CreateFromAxisAngle(axis, angle);
        return new Basis(Vector3.Transform(basis.X, roll), Vector3.Transform(basis.Y, roll), basis.Z);
    }

    private void Advance(float delta)
    {
        if (_speed == 0 || PathCurve is not { } curve)
            return;
        var length = curve.GetBakedLength();
        if (length <= 0)
            return;

        var next = _progress + _speed * _direction * delta;
        if (_loop || curve.Closed)
        {
            if (next >= length || next < 0)
                Looped?.Invoke();
            Progress = next;
            return;
        }

        if (next > length || next < 0)
        {
            if (PingPong)
            {
                next = next > length ? 2 * length - next : -next;
                _direction = -_direction;
                EndReached?.Invoke();
            }
            else if (!_atEnd)
            {
                _atEnd = true;
                EndReached?.Invoke();
            }
        }
        else
        {
            _atEnd = false;
        }

        Progress = next;
    }

    private float Constrain(float value)
    {
        if (!float.IsFinite(value))
            return 0f;
        if (PathCurve is not { } curve || curve.GetBakedLength() is var length && length <= 0)
            return value;
        if (_loop || curve.Closed)
        {
            var wrapped = value % length;
            return wrapped < 0 ? wrapped + length : wrapped;
        }

        return Math.Clamp(value, 0f, length);
    }

    private void UpdateCallbacks()
    {
        var moving = _speed != 0;
        SetProcess(moving && _processCallback == PathProcessCallback.Idle);
        SetPhysicsProcess(moving && _processCallback == PathProcessCallback.Physics);
    }
}
