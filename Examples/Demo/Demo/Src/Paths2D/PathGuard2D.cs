using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// A guard walking a patrol route by the "carrot" recipe (docs/design/paths.md): each frame the route's
/// <see cref="Carrot"/> follower is put <see cref="LookAhead"/> pixels past the guard's closest point on the curve, and
/// the guard steps toward it. It joins the route from wherever it starts and never needs code of its own for the shape.
/// </summary>
public sealed class PathGuard2D : Node2D
{
    private Path2D? _route;
    private PathFollow2D? _carrot;
    private float _lastOffset = float.NaN;

    /// <summary>The route (a <see cref="Path2D"/> with a <see cref="PathFollow2D"/> child named "Carrot").</summary>
    [Export] public NodePath Route { get; set; } = new("");

    /// <summary>Walking speed in pixels per second.</summary>
    [Export] public float Speed { get; set; } = 120f;

    /// <summary>How far ahead of the guard's closest point the carrot sits, in pixels.</summary>
    [Export] public float LookAhead { get; set; } = 36f;

    /// <summary>Pixels walked along the route while on it (laps = this / the route's length).</summary>
    public float Travelled { get; private set; }

    /// <summary>Whole laps walked.</summary>
    public int Laps => _route?.Curve?.GetBakedLength() is > 0 and var length ? (int)(Travelled / length) : 0;

    protected override void OnReady()
    {
        _route = GetNodeOrNull<Path2D>(Route);
        _carrot = _route?.GetNodeOrNull<PathFollow2D>("Carrot");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_route?.Curve is not { } curve || _carrot is null)
            return;
        var length = curve.GetBakedLength();
        if (length <= 0)
            return;
        var local = _route.ToLocal(GlobalPosition);
        var offset = curve.GetClosestOffset(local);
        _carrot.Progress = offset + LookAhead;

        var toCarrot = _carrot.GlobalPosition - GlobalPosition;
        var distance = toCarrot.Length();
        if (distance > 1e-3f)
        {
            GlobalPosition += toCarrot * (MathF.Min(Speed * gameTime.DeltaTime, distance) / distance);
            Rotation = MathF.Atan2(toCarrot.Y, toCarrot.X);
        }

        // Count progress along the route once the guard walks on it.
        if (Vector2.Distance(curve.GetClosestPoint(local), local) < 4f)
        {
            if (!float.IsNaN(_lastOffset))
            {
                var step = offset - _lastOffset;
                if (step < -length / 2)
                    step += length;
                else if (step > length / 2)
                    step -= length;
                Travelled += MathF.Max(0f, step);
            }

            _lastOffset = offset;
        }
    }

    protected override void OnExitTree()
    {
        _route = null;
        _carrot = null;
        base.OnExitTree();
    }
}
