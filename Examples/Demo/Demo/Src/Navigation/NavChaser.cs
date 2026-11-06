using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// A character body that walks wherever its <see cref="NavigationAgent2D"/> child leads (the engine's pattern: the body
/// asks the agent for the next path position and slides; the agent moves nothing). Slower in mud: the grid's weight scale
/// of the cell it stands in divides its speed. Chasers are on collision layer 2 and scan only layer 1 (walls), so they
/// pass through each other — unless their agents avoid (G3.7): then the body hands its velocity to
/// <see cref="NavigationAgent2D.SetVelocity"/> and slides by the safe velocity <see cref="NavigationAgent2D.VelocityComputed"/>
/// returns after the physics step, steering round the other chasers and the patrolling boulder. On a navigation mesh
/// (G3.9) it hops across the links its path takes.
/// </summary>
public sealed class NavChaser : CharacterBody2D
{
    private NavigationAgent2D? _agent;
    private NavigationGrid2D? _grid;

    [Export(Range = "0,1000,1")] public float Speed { get; set; } = 150f;

    /// <summary>The body's colour along the palette (0–1), for the builder.</summary>
    [Export(Range = "0,1,0.01")] public float Hue { get; set; }

    public NavigationAgent2D? Agent => _agent;

    /// <summary>A colour from the Demo's palette for <paramref name="t"/> (0–1).</summary>
    public static Vector4 ColorFor(float t)
    {
        Vector4[] palette = [new(0.98f, 0.8f, 0.08f, 1), new(0.2f, 0.83f, 0.6f, 1), new(0.27f, 0.55f, 0.94f, 1), new(0.91f, 0.36f, 0.25f, 1)];
        var f = Math.Clamp(t, 0f, 1f) * (palette.Length - 1);
        var i = Math.Min((int)f, palette.Length - 2);
        return Vector4.Lerp(palette[i], palette[i + 1], f - i);
    }

    protected override void OnReady()
    {
        _agent = GetNodeOrNull<NavigationAgent2D>("Agent");
        _grid = Parent?.Parent?.GetNodeOrNull<NavigationGrid2D>("Grid");
        if (_agent is { } agent)
        {
            agent.VelocityComputed += OnVelocityComputed;
            agent.LinkReached += OnLinkReached;
        }
    }

    // G3.9: a navigation link (the Navigation Mesh 2D scene's ferries) is crossed in one hop.
    private void OnLinkReached(NavigationLinkReached2D link) => GlobalPosition = link.ExitPosition;

    private void OnVelocityComputed(Vector2 safe)
    {
        Velocity = safe;
        MoveAndSlide();
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_agent is not { } agent)
            return;
        if (agent.IsNavigationFinished())
        {
            Velocity = Vector2.Zero;
            // Avoiding and arrived: hold the spot, so the body makes way for late arrivals (a chaser that only stood would
            // be a wall the others cannot get past) and walks back after.
            if (agent.AvoidanceEnabled && agent.IsTargetReached())
            {
                var back = agent.TargetPosition - GlobalPosition;
                agent.SetVelocity(back.LengthSquared() > 4f ? back * MathF.Min(4f, Speed / back.Length()) : Vector2.Zero);
            }

            return;
        }

        var to = agent.GetNextPathPosition() - GlobalPosition;
        var speed = Speed / MudFactor();
        // Avoidance on: the safe velocity comes back after the physics step (OnVelocityComputed); off: at once.
        agent.SetVelocity(to.LengthSquared() > 1e-4f ? Vector2.Normalize(to) * speed : Vector2.Zero);
    }

    private float MudFactor()
    {
        if (_grid is not { } grid)
            return 1f;
        var cell = grid.CellAt(GlobalPosition);
        var cells = grid.Grid;
        return cells.IsInBounds(cell) ? MathF.Max(1f, cells.GetPointWeightScale(cell)) : 1f;
    }
}
