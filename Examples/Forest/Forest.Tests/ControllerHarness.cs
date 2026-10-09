using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>
/// A headless scene tree with single-threaded 3D physics and the project's input map, ticked at a fixed 60 Hz: what
/// <see cref="FirstPersonController"/> needs without a window or GPU.
/// </summary>
internal sealed class ControllerHarness : IDisposable
{
    public const float Step = 1f / 60f;

    private uint _frame;

    /// <param name="floor">A 200 m floor box.</param>
    /// <param name="uiViewport">Also a headless <see cref="UiServer"/> of this size (RmlUi is process-global: such tests
    /// run in <see cref="MenuCollection"/>).</param>
    public ControllerHarness(bool floor = true, Vector2? uiViewport = null)
    {
        var servers = new ServerRegistry();
        servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        if (uiViewport is { } viewport)
            servers.Register(Ui = new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = viewport, ContentScale = 1f }));
        Tree = new SceneTree(servers);
        Tree.Input.Map = ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj")).Input;
        Scene = new Node3D { Name = "Scene" };
        Tree.ChangeScene(Scene);
        if (floor)
            AddBox(new Vector3(0, -0.5f, 0), new Vector3(200, 1, 200), name: "Floor");
    }

    public SceneTree Tree { get; }

    public UiServer? Ui { get; }

    public Node3D Scene { get; }

    public InputState Input => Tree.Input;

    public WaterQueries Water => Tree.Root.World3D.Water;

    public FirstPersonController AddPlayer(Vector3 feet = default, float yawDegrees = 0f, bool captureMouse = false)
    {
        var player = new FirstPersonController { Name = "Player", Position = feet + new Vector3(0, 0.02f, 0), CaptureMouse = captureMouse };
        Scene.AddChild(player);
        player.AddLook(yawDegrees, 0);
        return player;
    }

    public StaticBody3D AddBox(Vector3 centre, Vector3 size, Vector3 rotationDegrees = default, string? surface = null, string name = "Box")
    {
        StaticBody3D body = surface is null
            ? new StaticBody3D { Name = name, Position = centre, RotationDegrees = rotationDegrees }
            : new SurfaceBody3D { Name = name, Surface = surface, Position = centre, RotationDegrees = rotationDegrees };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        Scene.AddChild(body);
        return body;
    }

    /// <summary>A 4 m wide, 8 m long ramp rising at <paramref name="degrees"/> towards −Z, its foot at z = <paramref name="footZ"/>.</summary>
    public void AddRamp(float degrees, float footZ = -1f)
    {
        var angle = float.DegreesToRadians(degrees);
        const float length = 8f;
        var tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, angle);
        var topCentre = new Vector3(0, MathF.Sin(angle) * length / 2, footZ - MathF.Cos(angle) * length / 2);
        AddBox(topCentre - Vector3.Transform(Vector3.UnitY, tilt) * 0.2f, new Vector3(4, 0.4f, length), new Vector3(degrees, 0, 0), name: "Ramp");
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
    // test loop can fill one in without reflection (and without allocating, for the allocation gate).
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

/// <summary>A flat pool: still water with its surface at <paramref name="surface"/> over an XZ rectangle.</summary>
internal sealed class Pool(Vector2 min, Vector2 max, float surface, float bed) : IWaterBody3D
{
    public Aabb WaterBounds => new(new Vector3(min.X, bed, min.Y), new Vector3(max.X, surface, max.Y));

    public bool TrySample(Vector3 position, out WaterSample sample)
    {
        sample = new WaterSample(surface, surface - bed, Vector3.Zero);
        return position.X >= min.X && position.X <= max.X && position.Z >= min.Y && position.Z <= max.Y;
    }
}
