using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// Synthetic input that walks a <see cref="FirstPersonController"/> along the walking loop (<see cref="ValleyLayout"/>):
/// it presses <c>move_forward</c> and turns the view towards a point a few metres ahead on the path, sprints on part of
/// every lap and jumps now and then. Allocation-free after construction.
/// </summary>
public sealed class PathWalker
{
    /// <summary>How far ahead on the path the walker aims, in polyline points (about metres).</summary>
    public const int Lookahead = 4;

    /// <summary>Turn rate limit, degrees per second.</summary>
    public const float TurnRate = 150f;

    private readonly Vector2[] _path;
    private int _index;
    private float _time;
    private float _distance;

    public PathWalker(Vector2[] path, Vector2 start)
    {
        _path = path;
        var best = float.MaxValue;
        for (var i = 0; i < path.Length; i++)
        {
            var d = Vector2.DistanceSquared(path[i], start);
            if (d < best)
            {
                best = d;
                _index = i;
            }
        }

        StartIndex = _index;
    }

    public int StartIndex { get; }

    /// <summary>Path points passed since the start (one lap is the path's point count − 1).</summary>
    public int Progress { get; private set; }

    public bool LapComplete => Progress >= _path.Length - 1;

    /// <summary>Frames driven, and of those the frames the controller stood on the floor.</summary>
    public int Frames { get; private set; }

    public int FloorFrames { get; private set; }

    /// <summary>Metres the walker's feet moved horizontally.</summary>
    public float Distance => _distance;

    /// <summary>Presses the actions for this frame and turns the view.</summary>
    public void Drive(InputState input, FirstPersonController controller, float delta)
    {
        var position = controller.GlobalPosition;
        var p = new Vector2(position.X, position.Z);
        var n = _path.Length - 1; // the last point repeats the first
        while (Vector2.Distance(p, _path[_index]) < 2.5f && !LapComplete)
        {
            _index = (_index + 1) % n;
            Progress++;
        }

        var target = _path[(_index + Lookahead) % n];
        var to = target - p;
        var desired = float.RadiansToDegrees(MathF.Atan2(-to.X, -to.Y));
        var turn = MathF.IEEERemainder(desired - controller.YawDegrees, 360f);
        var limit = TurnRate * delta;
        controller.AddLook(Math.Clamp(turn, -limit, limit), -controller.PitchDegrees * MathF.Min(1f, 4f * delta));

        _time += delta;
        var cycle = _time % 40f;
        input.ActionPress("move_forward", 1f);
        if (cycle is > 20f and < 30f)
            input.ActionPress("sprint", 1f);
        else
            input.ActionRelease("sprint");
        if (cycle is > 12f and < 12.1f)
            input.ActionPress("jump", 1f);
        else
            input.ActionRelease("jump");
        var velocity = controller.Velocity;
        _distance += MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z) * delta;
        Frames++;
        if (controller.IsOnFloor())
            FloorFrames++;
    }

    public static void Release(InputState input)
    {
        input.ActionRelease("move_forward");
        input.ActionRelease("sprint");
        input.ActionRelease("jump");
    }
}
