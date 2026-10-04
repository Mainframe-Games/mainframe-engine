using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>A headless scene tree with a single-threaded 3D physics server, stepped at a fixed 60 Hz.</summary>
internal sealed class PhysicsHarness3D : IDisposable
{
    public const float Step = 1f / 60f;

    public PhysicsHarness3D(PhysicsSettings3D? settings = null)
    {
        Server = new PhysicsServer3D(settings ?? new PhysicsSettings3D { MultiThreaded = false });
        var servers = new ServerRegistry();
        servers.Register(Server);
        Tree = new SceneTree(servers);
        Scene = new Node3D { Name = "Scene" };
        Tree.ChangeScene(Scene);
    }

    public SceneTree Tree { get; }
    public PhysicsServer3D Server { get; }
    public Node3D Scene { get; }
    public PhysicsSpace3D Space => Server.Spaces[0];
    public PhysicsDirectSpaceState3D Query => Tree.Root.World3D.DirectSpaceState;

    /// <summary>Runs <paramref name="frames"/> frames of <paramref name="delta"/> seconds (default: one physics step each).</summary>
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

    public StaticBody3D AddFloor(float y = 0, float size = 40f, uint layer = CollisionLayers.Default, uint mask = CollisionLayers.Default)
    {
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, y - 0.5f, 0), CollisionLayer = layer, CollisionMask = mask };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size, 1, size) } });
        return Add(floor);
    }

    public RigidBody3D AddBox(Vector3 position, float size = 1f, string name = "Box")
    {
        var box = new RigidBody3D { Name = name, Position = position };
        box.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(size) } });
        return Add(box);
    }

    public StaticBody3D AddStaticBox(Vector3 position, Vector3 size, Vector3 rotationDegrees = default, string name = "Wall")
    {
        var body = new StaticBody3D { Name = name, Position = position, RotationDegrees = rotationDegrees };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return Add(body);
    }

    public Area3D AddArea(Vector3 position, Vector3 size, string name = "Area")
    {
        var area = new Area3D { Name = name, Position = position };
        area.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return Add(area);
    }

    public void Dispose()
    {
        Tree.Shutdown();
        Tree.Servers.Dispose();
    }
}

/// <summary>A character that applies gravity and a fixed horizontal velocity, then moves and slides every physics step.</summary>
internal sealed class WalkerCharacter3D : CharacterBody3D
{
    public Vector3 Walk { get; set; }
    public float Gravity { get; set; } = 9.81f;
    public int Moves { get; private set; }

    protected override void OnPhysicsProcess(float delta)
    {
        var velocity = Velocity;
        velocity.X = Walk.X;
        velocity.Z = Walk.Z;
        velocity.Y -= Gravity * delta;
        Velocity = velocity;
        MoveAndSlide();
        Moves++;
    }
}

/// <summary>Records the pose a body has while <see cref="Node.OnPhysicsProcess"/> runs.</summary>
internal sealed class PhysicsPoseProbe(Node3D target) : Node
{
    public List<Vector3> Seen { get; } = [];

    protected override void OnPhysicsProcess(float delta) => Seen.Add(target.Position);
}
