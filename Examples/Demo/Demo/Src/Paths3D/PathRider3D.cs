using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Demo;

/// <summary>
/// The Paths 3D scene's capsule: WASD / arrows / left stick walk it (the <c>move_*</c> actions, along the world's X and
/// Z), Space or <see cref="Jump"/> jumps. It rides the lifts, the ferry and the turntable as a
/// <see cref="CharacterBody3D"/> does (G13.5), and keeps their velocity when it jumps off.
/// </summary>
public sealed class PathRider3D : CharacterBody3D
{
    private bool _jumpQueued;
    private bool _spaceWasDown;

    /// <summary>Walking speed in m/s.</summary>
    [Export(Range = "0,20,0.1")] public float WalkSpeed { get; set; } = 4f;

    /// <summary>Take-off speed of a jump in m/s.</summary>
    [Export(Range = "0,20,0.1")] public float JumpSpeed { get; set; } = 5.5f;

    /// <summary>Jumps at the next physics step if the rider is on the floor.</summary>
    public void Jump() => _jumpQueued = true;

    protected override void OnPhysicsProcess(float delta)
    {
        var walk = Vector2.Zero;
        if (Tree is { } tree)
        {
            walk = tree.Input.GetVector("move_left", "move_right", "move_up", "move_down");
            var space = tree.Input.IsKeyPressed(Key.Space);
            if (space && !_spaceWasDown)
                _jumpQueued = true;
            _spaceWasDown = space;
        }

        var velocity = Velocity;
        velocity.X = walk.X * WalkSpeed;
        velocity.Z = walk.Y * WalkSpeed;
        velocity.Y -= 9.81f * delta;
        if (_jumpQueued && IsOnFloor())
            velocity.Y = JumpSpeed;
        _jumpQueued = false;
        Velocity = velocity;
        MoveAndSlide();
        if (GlobalPosition.Y < -20f)
        {
            GlobalPosition = new Vector3(-8f, 1.5f, 0f); // fell off the world: back onto the lift
            Velocity = Vector3.Zero;
        }
    }
}
