using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// G3.11: a capsule character that walks wherever its <see cref="NavigationAgent3D"/> child leads — level, at
/// <see cref="Speed"/>, with gravity — and stands once the agent finishes. The agent's
/// <see cref="NavigationAgent3D.PathHeightOffset"/> is the capsule's half height, so distances are measured on the floor.
/// </summary>
public sealed class NavChaser3D : CharacterBody3D
{
    private NavigationAgent3D? _agent;

    [Export(Range = "0,20,0.1")] public float Speed { get; set; } = 3.5f;

    public NavigationAgent3D? Agent => _agent;

    protected override void OnReady() => _agent = GetNodeOrNull<NavigationAgent3D>("Agent");

    protected override void OnPhysicsProcess(float delta)
    {
        var fall = Velocity.Y - 9.8f * delta;
        var horizontal = Vector3.Zero;
        if (_agent is { } agent && !agent.IsNavigationFinished())
        {
            var to = agent.GetNextPathPosition() - GlobalPosition;
            to.Y = 0f;
            if (to.LengthSquared() > 1e-6f)
                horizontal = Vector3.Normalize(to) * Speed;
        }

        Velocity = new Vector3(horizontal.X, IsOnFloor() ? MathF.Max(fall, -1f) : fall, horizontal.Z);
        MoveAndSlide();
    }
}
