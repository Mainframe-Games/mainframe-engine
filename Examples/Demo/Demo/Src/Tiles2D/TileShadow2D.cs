using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>
/// The Tiles 2D shadow (G3.6): a dark copy of the hero that chases it. Its <see cref="NavigationAgent2D"/> child paths on
/// the scene's <see cref="NavigationGrid2D"/>, which reads the "Walls" layer (stone and water) with the
/// <see cref="NavigationGridSource.TileMapLayer"/> source, so the shadow goes round walls and water and repaths when a
/// click adds or removes a wall. It is not a body: it walks the path (through bushes, as shadows do) and waits a step
/// away from the hero.
/// </summary>
public sealed class TileShadow2D : Node2D
{
    private NavigationAgent2D? _agent;
    private AnimatedSprite2D? _sprite;
    private Node2D? _hero;

    /// <summary>Walk speed in pixels per second (a little slower than the hero, so it never quite catches up).</summary>
    [Export(Range = "0,400,1")]
    public float Speed { get; set; } = 55f;

    /// <summary>Follows the hero; off, it stands still.</summary>
    [Export]
    public bool Chasing { get; set; } = true;

    /// <summary>How close (pixels) it comes before it waits.</summary>
    [Export(Range = "0,64,1")]
    public float CatchDistance { get; set; } = 14f;

    public NavigationAgent2D? Agent => _agent;

    protected override void OnReady()
    {
        _agent = GetNodeOrNull<NavigationAgent2D>("Agent");
        _sprite = GetNodeOrNull<AnimatedSprite2D>("Sprite");
        _hero = GetNodeOrNull<Node2D>("../Hero");
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_agent is not { } agent || _hero is not { } hero)
            return;
        agent.TargetPosition = hero.GlobalPosition;
        var step = Vector2.Zero;
        if (Chasing && Vector2.Distance(GlobalPosition, hero.GlobalPosition) > CatchDistance && !agent.IsNavigationFinished())
        {
            var to = agent.GetNextPathPosition() - GlobalPosition;
            var length = to.Length();
            var stride = Speed * delta;
            if (length > 1e-3f)
                step = length <= stride ? to : to / length * stride;
            GlobalPosition += step;
        }

        if (_sprite is { } sprite)
        {
            var animation = step.LengthSquared() > 1e-4f ? "run" : "idle";
            if (sprite.Animation != animation)
                sprite.Play(animation);
            if (MathF.Abs(step.X) > 1e-3f)
                sprite.FlipH = step.X < 0;
        }
    }
}
