using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Box2D keeps its worlds in a process-wide table: tests that create 2D worlds don't run in parallel.</summary>
[CollectionDefinition(nameof(SerialBox2D), DisableParallelization = true)]
public sealed class SerialBox2D;

/// <summary>A headless scene tree with a 2D physics server (100 px/m, 980 px/s² down = +Y: 2D is Y-down), stepped at 60 Hz.</summary>
internal sealed class PhysicsHarness2D : IDisposable
{
    public const float Step = 1f / 60f;

    public PhysicsHarness2D(PhysicsSettings2D? settings = null)
    {
        Server = new PhysicsServer2D(settings ?? new PhysicsSettings2D());
        var servers = new ServerRegistry();
        servers.Register(Server);
        Tree = new SceneTree(servers);
        Scene = new Node2D { Name = "Scene" };
        Tree.ChangeScene(Scene);
    }

    public SceneTree Tree { get; }
    public PhysicsServer2D Server { get; }
    public Node2D Scene { get; }
    public PhysicsSpace2D Space => Server.Spaces[0];
    public PhysicsDirectSpaceState2D Query => Tree.Root.World2D.DirectSpaceState;

    public void Run(int frames, float delta = Step)
    {
        var time = new GameTime { DeltaTime = delta };
        for (var i = 0; i < frames; i++)
            Tree.Tick(time);
    }

    public void RunSeconds(float seconds) => Run((int)MathF.Round(seconds / Step));

    public T Add<T>(T node) where T : Node
    {
        Scene.AddChild(node);
        return node;
    }

    /// <summary>A static ground whose top is at <paramref name="y"/> (pixels; the ground extends down, +Y).</summary>
    public StaticBody2D AddGround(float y = 0, float width = 4000, uint layer = CollisionLayers.Default, uint mask = CollisionLayers.Default)
    {
        var ground = new StaticBody2D { Name = "Ground", Position = new Vector2(0, y + 50), CollisionLayer = layer, CollisionMask = mask };
        ground.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(width, 100) } });
        return Add(ground);
    }

    public RigidBody2D AddBox(Vector2 position, float size = 50, string name = "Box")
    {
        var box = new RigidBody2D { Name = name, Position = position };
        box.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(size) } });
        return Add(box);
    }

    public StaticBody2D AddStaticBox(Vector2 position, Vector2 size, float rotationDegrees = 0, string name = "Wall")
    {
        var body = new StaticBody2D { Name = name, Position = position, RotationDegrees = rotationDegrees };
        body.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = size } });
        return Add(body);
    }

    public Area2D AddArea(Vector2 position, Vector2 size, string name = "Area")
    {
        var area = new Area2D { Name = name, Position = position };
        area.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = size } });
        return Add(area);
    }

    public void Dispose()
    {
        Tree.Shutdown();
        Tree.Servers.Dispose();
    }
}

internal sealed class WalkerCharacter2D : CharacterBody2D
{
    public Vector2 Walk { get; set; }
    public float Gravity { get; set; } = 980f;

    protected override void OnPhysicsProcess(float delta)
    {
        var velocity = Velocity;
        velocity.X = Walk.X;
        velocity.Y += Gravity * delta; // Y-down: gravity pulls +Y
        Velocity = velocity;
        MoveAndSlide();
    }
}

internal sealed class FloaterCharacter2D : CharacterBody2D
{
    public Vector2 Walk { get; set; }

    protected override void OnPhysicsProcess(float delta)
    {
        Velocity = Walk;
        MoveAndSlide();
    }
}
