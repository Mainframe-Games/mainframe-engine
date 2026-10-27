using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// A headless scene tree with single-threaded 3D physics and the project's input map, ticked at a fixed 60 Hz: what
/// <see cref="FirstPersonController"/> and the interactables need without a window or GPU (the Forest's harness).
/// </summary>
internal sealed class ControllerHarness : IDisposable
{
    public const float Step = 1f / 60f;

    private uint _frame;

    /// <param name="floor">A 200 m floor box.</param>
    public ControllerHarness(bool floor = true)
    {
        var servers = new ServerRegistry();
        servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        Tree = new SceneTree(servers);
        Tree.Input.Map = ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj")).Input;
        Scene = new Node3D { Name = "Scene" };
        Tree.ChangeScene(Scene);
        if (floor)
            AddBox(new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200), name: "Floor");
    }

    public SceneTree Tree { get; }

    public Node3D Scene { get; }

    public InputState Input => Tree.Input;

    /// <summary>The player's state when the last <see cref="WalkTo"/> gave up.</summary>
    public string Blocked { get; private set; } = "";

    /// <summary>A player with its <see cref="Interactor"/>, feet at <paramref name="feet"/>, turned <paramref name="yawDegrees"/> (0 looks along −Z).</summary>
    public FirstPersonController AddPlayer(Vector3 feet = default, float yawDegrees = 0f, float pitchDegrees = 0f)
    {
        var player = new FirstPersonController { Name = "Player", Position = feet + new Vector3(0, 0.02f, 0), CaptureMouse = false, FloorSnapLength = 0.35f };
        Scene.AddChild(player);
        player.AddChild(new Interactor { Name = "Interactor" });
        player.AddLook(yawDegrees, pitchDegrees);
        return player;
    }

    public StaticBody3D AddBox(Vector3 centre, Vector3 size, string name = "Box")
    {
        var body = new StaticBody3D { Name = name, Position = centre };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        Scene.AddChild(body);
        return body;
    }

    /// <summary>A 3 m high wall along X at z = 0 with a 1 m wide, 2.2 m high opening at x ∈ [−0.5, 0.5].</summary>
    public void AddWallWithOpening()
    {
        AddBox(new Vector3(-3.5f, 1.5f, 0f), new Vector3(6f, 3f, 0.2f), "WallWest");
        AddBox(new Vector3(3.5f, 1.5f, 0f), new Vector3(6f, 3f, 0.2f), "WallEast");
        AddBox(new Vector3(0f, 2.6f, 0f), new Vector3(1f, 0.8f, 0.2f), "Lintel");
    }

    /// <summary>Runs <paramref name="frames"/> frames of 1/60 s (one physics step each).</summary>
    public void Run(int frames)
    {
        for (var i = 0; i < frames; i++)
            TestTime.Tick(Tree, ref _frame);
    }

    public void RunSeconds(float seconds) => Run((int)MathF.Round(seconds / Step));

    public void Hold(string action, float strength = 1f) => Input.ActionPress(action, strength);

    public void Release(string action) => Input.ActionRelease(action);

    /// <summary>Presses and releases <c>interact</c> (one frame each).</summary>
    public void PressInteract()
    {
        Hold("interact");
        Run(1);
        Release("interact");
        Run(1);
    }

    /// <summary>Turns the player to face <paramref name="yawDegrees"/> (0 looks along −Z, 90 along −X).</summary>
    public static void Face(FirstPersonController player, float yawDegrees) =>
        player.AddLook(MathF.IEEERemainder(yawDegrees - player.YawDegrees, 360f), 0f);

    /// <summary>
    /// Walks the player towards <paramref name="target"/> (feet, XZ) for at most <paramref name="seconds"/>, steering
    /// every frame; true when it got within <paramref name="tolerance"/>.
    /// </summary>
    public bool WalkTo(FirstPersonController player, Vector2 target, float seconds = 8f, float tolerance = 0.3f)
    {
        var frames = (int)(seconds / Step);
        try
        {
            for (var i = 0; i < frames; i++)
            {
                var p = player.GlobalPosition;
                var d = target - new Vector2(p.X, p.Z);
                if (d.Length() < tolerance)
                    return true;
                Face(player, float.RadiansToDegrees(MathF.Atan2(-d.X, -d.Y)));
                Hold("move_forward", d.Length() < 0.6f ? 0.5f : 1f);
                Run(1);
            }

            var end = player.GlobalPosition;
            // What it was up against, for the failure message.
            Blocked = $"velocity {player.Velocity}, on the floor: {player.IsOnFloor()}";
            for (var c = 0; c < player.GetSlideCollisionCount(); c++)
                Blocked += $"; touching {player.GetSlideCollision(c).Collider?.Name} at {player.GetSlideCollision(c).Position} (normal {player.GetSlideCollision(c).Normal})";
            return (target - new Vector2(end.X, end.Z)).Length() < tolerance;
        }
        finally
        {
            Release("move_forward");
            Run(2);
        }
    }

    public void Dispose()
    {
        Tree.Shutdown();
        Tree.Servers.Dispose();
    }
}

/// <summary>Ticks a <see cref="SceneTree"/> with a fixed 60 Hz <see cref="GameTime"/> (what the game loop would pass).</summary>
internal static class TestTime
{
    // GameTime's setters are internal to the engine; this mirror has its layout (sequential auto-property fields), so a
    // test loop can fill one in without reflection (and without allocating).
    private struct GameTimeLayout
    {
        public uint FrameCount;
        public float DeltaTime;
        public uint FramesPerSecond;
        public uint FramesTimeMs;
    }

    public static void Tick(SceneTree tree, ref uint frame)
    {
        var layout = new GameTimeLayout { FrameCount = ++frame, DeltaTime = ControllerHarness.Step, FramesPerSecond = 60, FramesTimeMs = 16 };
        tree.Tick(System.Runtime.CompilerServices.Unsafe.As<GameTimeLayout, GameTime>(ref layout));
    }
}
