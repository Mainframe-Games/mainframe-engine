using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Physics3DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly PhysicsServer3D _physics = new(new PhysicsSettings3D { MultiThreaded = false });
    private uint _frame;

    public Physics3DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(_physics);
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(Physics3DScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private Dropper3D Crates => Scene.GetNode<Dropper3D>("Crates");

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheSceneHoldsFloorRampPusherAndCrates()
    {
        Assert.NotNull(Scene.GetNode<StaticBody3D>("Floor"));
        Assert.NotNull(Scene.GetNode<StaticBody3D>("Ramp"));
        Assert.NotNull(Scene.GetNode<Pusher3D>("Pusher"));
        Assert.NotNull(Crates);
    }

    [Fact]
    public void TheStackStartsWithFifteenCratesAndResetRestoresIt()
    {
        Assert.Equal(15, Crates.Count);
        Crates.Drop(new Vector3(0, 6, 0));
        Crates.Drop(new Vector3(1, 7, 0));
        Assert.Equal(17, Crates.Count);
        Crates.ResetBodies();
        Assert.Equal(15, Crates.Count);
    }

    [Fact]
    public void ADroppedCrateFalls()
    {
        var crate = Crates.Drop(new Vector3(0, 8, 3));
        Tick();
        var start = crate.Position.Y;
        for (var i = 0; i < 30; i++)
            Tick();
        Assert.True(crate.Position.Y < start - 0.5f, $"crate stayed at {crate.Position.Y} (from {start})");
    }

    [Fact]
    public void CratesLandOnTheFloorAndOnEachOther()
    {
        var lower = Crates.Drop(new Vector3(-9, 0.6f, -4));
        var upper = Crates.Drop(new Vector3(-9, 2f, -4));
        for (var i = 0; i < 120; i++)
            Tick();
        Assert.InRange(lower.Position.Y, 0.35f, 0.45f);
        Assert.InRange(upper.Position.Y - lower.Position.Y, 0.75f, 0.85f);
    }

    [Fact]
    public void ThePusherSlidesAlongTheFloorAndShovesTheStack()
    {
        var pusher = Scene.GetNode<Pusher3D>("Pusher");
        var firstCrate = (Node3D)Crates.GetChild(0);
        var crateStart = firstCrate.Position;
        var start = pusher.Position;
        for (var i = 0; i < 180; i++)
            Tick();
        Assert.True(pusher.Position.X > start.X + 2f, $"pusher stayed at {pusher.Position}");
        Assert.InRange(pusher.Position.Y, start.Y - 0.1f, start.Y + 0.1f); // stays on the floor
        Assert.True(firstCrate.Position.X > crateStart.X + 0.2f, $"crate stayed at {firstCrate.Position}");
    }

    // The click path minus the mouse: the camera's ray through a framebuffer pixel, cast into the physics world.
    private RayHit3D CastThrough(Vector2 pixel)
    {
        var camera = Scene.GetNode<Camera3D>("Camera");
        var size = new Vector2(1280, 720);
        var (origin, direction) = Camera3D.ProjectRay(camera.SyncRenderCamera(size.X / size.Y), pixel, size);
        Assert.True(_tree.Root.World3D.DirectSpaceState.RayCast(origin, origin + direction * 200f, out var hit));
        return hit;
    }

    [Fact]
    public void ARayThroughTheScreenCentreHitsTheCrateStack()
    {
        Tick();
        Assert.Equal(Crates, CastThrough(new Vector2(640, 360)).Collider.Parent);
    }

    [Fact]
    public void ARayThroughTheLowerScreenHitsTheFloor()
    {
        Tick();
        var hit = CastThrough(new Vector2(640, 650));
        Assert.Equal("Floor", hit.Collider.Name);
        Assert.Equal(0f, hit.Position.Y, 2);
    }

    [Fact]
    public void LeavingTheSceneRestoresTheCollisionShapeOverlay()
    {
        // Enter a fresh scene with the overlay on, turn it off (what the checkbox does), then leave.
        _tree.ChangeScene(new Node3D { Name = "Other" });
        _physics.DebugDrawEnabled = true;
        _tree.ChangeScene(Physics3DScene.Build());
        _physics.DebugDrawEnabled = false;
        _tree.ChangeScene(new Node3D { Name = "Other2" });
        Assert.True(_physics.DebugDrawEnabled);
    }
}
