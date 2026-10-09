using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The <c>Dev</c> autoload: developer switches passed as game arguments after <c>++</c>. Idle without them.
/// <list type="bullet">
/// <item><c>--autowalk</c>: synthetic input walks a fixed loop (<see cref="AutoWalkPath"/>: walking, turning, sprinting,
/// a jump, wading through the stream), then measures what a 600-frame window allocates on the main thread after a
/// 180-frame warm-up and quits (exit code 0 when it allocated nothing, 1 otherwise). With <c>--fixed-fps</c> the walk is
/// the same on every run.</item>
/// <item><c>--no-capture</c>: the controller does not capture the mouse (scripted runs, screenshots).</item>
/// </list>
/// </summary>
public sealed class ForestDev : Node
{
    public const int WarmUpFrames = 180;
    public const int WindowFrames = 600;

    private FirstPersonController? _controller;
    private bool _autoWalk;
    private int _frame;
    private long _allocatedBefore;
    private int _collectionsBefore;
    private int _footstepsBefore;
    private float _time;

    /// <summary>One leg of the walk: hold a move vector (x right, y back), turn, sprint, jump at its start.</summary>
    public readonly record struct Leg(float Seconds, Vector2 Move, float TurnDegreesPerSecond = 0, bool Sprint = false, bool Jump = false);

    /// <summary>The loop <c>--autowalk</c> repeats (no crouch: a crouch rebuilds the capsule, which is not per-frame work).</summary>
    public static readonly Leg[] AutoWalkPath =
    [
        new(3.2f, new Vector2(0, -1)),                                   // towards the stream, down into the trench
        new(2.5f, new Vector2(0, -1), TurnDegreesPerSecond: 36f),        // wade, turning upstream
        new(3.0f, new Vector2(0, -1)),                                   // wade south, against the flow
        new(1.5f, new Vector2(0, -1), Jump: true),                       // a jump in the water
        new(4.0f, new Vector2(0, -1), Sprint: true),                     // up the south ramp
        new(2.0f, new Vector2(0, -1), TurnDegreesPerSecond: 90f),        // turn back west
        new(3.0f, new Vector2(0.6f, -0.8f), Sprint: true),               // run diagonally
        new(2.0f, new Vector2(0, -1), TurnDegreesPerSecond: -60f),       // curve
    ];

    /// <summary>True when the game was started with <c>++ --autowalk</c>.</summary>
    public bool AutoWalk => _autoWalk;

    protected override void OnReady()
    {
        _autoWalk = GameHost.UserArgs.Contains("--autowalk");
        if (_autoWalk)
            Log.Info($"[Forest] --autowalk: {WarmUpFrames} warm-up frames, then a {WindowFrames}-frame allocation window.");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_autoWalk || Tree is not { } tree)
            return;
        _controller ??= Find(tree.Root);
        if (_controller is null)
            return;

        Drive(tree.Input, _controller, _time, gameTime.DeltaTime);
        _time += gameTime.DeltaTime;
        _frame++;
        if (_frame == WarmUpFrames)
        {
            _allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            _collectionsBefore = GC.CollectionCount(0);
            _footstepsBefore = _controller.FootstepCount;
        }
        else if (_frame == WarmUpFrames + WindowFrames)
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocatedBefore;
            var collections = GC.CollectionCount(0) - _collectionsBefore;
            var steps = _controller.FootstepCount - _footstepsBefore;
            if (allocated == 0)
                Log.Info($"[Forest] autowalk: {WindowFrames} frames allocated 0 B ({steps} footsteps, last on {_controller.LastFootstepSurface}, {collections} gen-0 GCs).");
            else
                Log.Error($"[Forest] autowalk: {WindowFrames} frames allocated {allocated} B ({steps} footsteps, {collections} gen-0 GCs).");
            Release(tree.Input);
            tree.Quit(allocated == 0 ? 0 : 1);
        }
    }

    /// <summary>Presses the actions for the walk at <paramref name="time"/> seconds and turns the view.</summary>
    public static void Drive(InputState input, FirstPersonController controller, float time, float delta)
    {
        var total = 0f;
        foreach (var leg in AutoWalkPath)
            total += leg.Seconds;
        var t = time % total;
        var legStart = 0f;
        var current = AutoWalkPath[0];
        foreach (var leg in AutoWalkPath)
        {
            if (t < legStart + leg.Seconds)
            {
                current = leg;
                break;
            }

            legStart += leg.Seconds;
        }

        Press(input, "move_right", MathF.Max(current.Move.X, 0f));
        Press(input, "move_left", MathF.Max(-current.Move.X, 0f));
        Press(input, "move_back", MathF.Max(current.Move.Y, 0f));
        Press(input, "move_forward", MathF.Max(-current.Move.Y, 0f));
        Press(input, "sprint", current.Sprint ? 1f : 0f);
        Press(input, "jump", current.Jump && t - legStart < 0.1f ? 1f : 0f);
        if (current.TurnDegreesPerSecond != 0)
            controller.AddLook(-current.TurnDegreesPerSecond * delta, 0f);
    }

    private static void Press(InputState input, string action, float strength)
    {
        if (strength > 0f)
            input.ActionPress(action, strength);
        else
            input.ActionRelease(action);
    }

    private static void Release(InputState input)
    {
        foreach (var action in (ReadOnlySpan<string>)["move_right", "move_left", "move_back", "move_forward", "sprint", "jump"])
            input.ActionRelease(action);
    }

    private static FirstPersonController? Find(Node node)
    {
        if (node is FirstPersonController controller)
            return controller;
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            if (Find(children[i]) is { } found)
                return found;
        }

        return null;
    }
}
