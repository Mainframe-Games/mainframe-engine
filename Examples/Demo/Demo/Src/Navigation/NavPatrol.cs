using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// A moving avoidance obstacle (G3.7): a <see cref="NavigationObstacle2D"/> that rolls between <see cref="PointA"/> and
/// <see cref="PointB"/> at <see cref="Speed"/>, setting its <see cref="NavigationObstacle2D.Velocity"/> every tick so
/// avoiding agents predict where it goes. It is no physics body: only avoidance keeps the chasers off it.
/// </summary>
public sealed class NavPatrol : NavigationObstacle2D
{
    private bool _toB = true;

    [Export] public Vector2 PointA { get; set; }

    [Export] public Vector2 PointB { get; set; }

    [Export(Range = "0,1000,1")] public float Speed { get; set; } = 60f;

    protected override void OnPhysicsProcess(float delta)
    {
        var goal = _toB ? PointB : PointA;
        var to = goal - GlobalPosition;
        var step = Speed * delta;
        if (to.Length() <= step)
        {
            GlobalPosition = goal;
            _toB = !_toB;
            Velocity = Vector2.Zero;
            return;
        }

        Velocity = Vector2.Normalize(to) * Speed;
        GlobalPosition += Velocity * delta;
    }
}
