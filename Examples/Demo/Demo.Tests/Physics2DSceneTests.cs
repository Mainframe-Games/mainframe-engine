using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class Physics2DSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private readonly PhysicsServer2D _physics = new(new PhysicsSettings2D());
    private uint _frame;

    public Physics2DSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _servers.Register(_physics);
        _tree = new SceneTree(_servers);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void SpawnedBodiesFallTowardsPositiveY()
    {
        var spawner = new Spawner2D { Name = "Bodies", SpawnStack = false };
        _tree.Root.AddChild(spawner);
        var body = spawner.Spawn(new Vector2(0, 0), circle: true);

        for (var i = 0; i < 30; i++)
            Tick();

        Assert.True(body.Position.Y > 10f, body.Position.ToString());
    }

    [Fact]
    public void TheSceneHoldsGroundFunnelWallsAndPegs()
    {
        _tree.ChangeScene(Physics2DScene.Build());
        var scene = _tree.CurrentScene!;
        foreach (var name in new[] { "Ground", "LeftFunnel", "RightFunnel", "LeftWall", "RightWall" })
            Assert.NotNull(scene.GetNode<StaticBody2D>(name));
        // Four rows alternating 7 and 6 pegs.
        Assert.Equal(26, scene.Children.Count(static c => c.Name.ToString().StartsWith("Peg", StringComparison.Ordinal)));
        Assert.NotNull(scene.GetNode<Spawner2D>("Bodies"));
    }

    [Fact]
    public void ResetEmptiesTheSpawnerAndRainsFortyBodiesBackIn()
    {
        _tree.ChangeScene(Physics2DScene.Build());
        var bodies = _tree.CurrentScene!.GetNode<Spawner2D>("Bodies");
        for (var i = 0; i < 40; i++)
            Tick();
        Assert.Equal(40, bodies.Count);

        bodies.ResetBodies();
        Assert.Equal(0, bodies.Count);
        for (var i = 0; i < 40; i++)
            Tick();
        Assert.Equal(40, bodies.Count);
        Tick();
        Assert.Equal(40, bodies.Count); // the rain stops
    }

    [Fact]
    public void RainedBodiesLandOnTheGroundAndStayAboveIt()
    {
        _tree.ChangeScene(Physics2DScene.Build());
        var bodies = _tree.CurrentScene!.GetNode<Spawner2D>("Bodies");
        for (var i = 0; i < 600; i++)
            Tick();
        foreach (var child in bodies.Children)
            Assert.InRange(((RigidBody2D)child).Position.Y, -500f, 330f);
    }

    [Fact]
    public void LeavingTheSceneRestoresTheCollisionShapeOverlay()
    {
        _tree.ChangeScene(new Node2D { Name = "Other" });
        _physics.DebugDrawEnabled = true;
        _tree.ChangeScene(Physics2DScene.Build());
        _physics.DebugDrawEnabled = false; // what the checkbox does
        _tree.ChangeScene(new Node2D { Name = "Other2" });
        Assert.True(_physics.DebugDrawEnabled);
    }
}
