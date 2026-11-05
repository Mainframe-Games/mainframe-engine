using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// A guard walking a patrol route by the carrot recipe in 3D (docs/design/paths.md): the route's "Carrot" follower is put
/// <see cref="LookAhead"/> metres past the guard's closest point, found with the hinted query near where it was last
/// frame, and the guard (a <see cref="CharacterBody3D"/>) walks toward it with gravity, so it rounds obstacles' corners
/// and climbs steps a free path would not.
/// </summary>
public sealed class PathGuard3D : CharacterBody3D
{
    private Path3D? _route;
    private PathFollow3D? _carrot;
    private float _offset = float.NaN;

    /// <summary>The route (a <see cref="Path3D"/> with a <see cref="PathFollow3D"/> child named "Carrot").</summary>
    [Export] public NodePath Route { get; set; } = new("");

    [Export(Range = "0,20,0.1")] public float Speed { get; set; } = 2.5f;

    [Export(Range = "0,20,0.1")] public float LookAhead { get; set; } = 1.5f;

    /// <summary>Metres walked along the route while on it.</summary>
    public float Travelled { get; private set; }

    /// <summary>Whole laps walked.</summary>
    public int Laps => _route?.Curve?.GetBakedLength() is > 0 and var length ? (int)(Travelled / length) : 0;

    protected override void OnReady()
    {
        _route = GetNodeOrNull<Path3D>(Route);
        _carrot = _route?.GetNodeOrNull<PathFollow3D>("Carrot");
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_route?.Curve is not { } curve || _carrot is null || curve.GetBakedLength() is var length && length <= 0)
            return;
        var local = _route.GlobalTransform.AffineInverse().TransformPoint(GlobalPosition);
        var flat = new Vector3(local.X, curve.GetClosestPoint(local).Y, local.Z); // the route's height, not the body's
        var offset = float.IsNaN(_offset) ? curve.GetClosestOffset(flat) : curve.GetClosestOffset(flat, _offset, 4f);
        if (!float.IsNaN(_offset) && Vector3.Distance(curve.SampleBaked(offset), flat) < 0.3f)
        {
            var step = offset - _offset;
            if (step < -length / 2)
                step += length;
            else if (step > length / 2)
                step -= length;
            Travelled += MathF.Max(0f, step);
        }

        _offset = offset;
        _carrot.Progress = offset + LookAhead;
        var to = _carrot.GlobalPosition - GlobalPosition;
        to.Y = 0;
        var walk = to.LengthSquared() > 1e-4f ? Vector3.Normalize(to) * Speed : Vector3.Zero;
        Velocity = new Vector3(walk.X, Velocity.Y - 9.81f * delta, walk.Z);
        MoveAndSlide();
        if (walk != Vector3.Zero)
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-walk.X, -walk.Z));
    }
}
