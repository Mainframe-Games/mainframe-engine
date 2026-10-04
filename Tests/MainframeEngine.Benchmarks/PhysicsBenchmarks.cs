using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Physics at scale (M6): one fixed step of 1 000 awake rigid bodies resting on a floor in 3D (Jitter2, worker pool) and
/// in 2D (Box2D.NET), each through the scene tree (push, step, pose pull, interpolation); and 10 000 raycasts into the
/// 3D world. Bodies never sleep, so every step does the full contact-solving work.
/// </summary>
[MemoryDiagnoser]
public class PhysicsBenchmarks : IDisposable
{
    private const int Bodies = 1000;
    private const int Rays = 10_000;

    private SceneTree _tree3D = null!;
    private SceneTree _tree2D = null!;
    private PhysicsDirectSpaceState3D _query = null!;
    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };

    [GlobalSetup]
    public void Setup()
    {
        // 3D: a 40 × 25 grid of 1 m boxes on a static floor.
        _tree3D = NewTree(new PhysicsServer3D());
        var scene3D = new Node3D { Name = "Scene3D" };
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(200, 1, 200) } });
        scene3D.AddChild(floor);
        var cube = new BoxShape3D();
        for (var i = 0; i < Bodies; i++)
        {
            var body = new RigidBody3D { Position = new Vector3(i % 40 * 1.5f - 30, 0.5f, i / 40 * 1.5f - 18), CanSleep = false };
            body.AddChild(new CollisionShape3D { Shape = cube });
            scene3D.AddChild(body);
        }

        _tree3D.ChangeScene(scene3D);
        _query = _tree3D.Root.World3D.DirectSpaceState;

        // 2D: 1 000 boxes of 50 px in five rows on a static ground.
        _tree2D = NewTree(new PhysicsServer2D());
        var scene2D = new Node2D { Name = "Scene2D" };
        var ground = new StaticBody2D { Name = "Ground", Position = new Vector2(0, -50) };
        ground.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(30000, 100) } });
        scene2D.AddChild(ground);
        var square = new RectangleShape2D { Size = new Vector2(50, 50) };
        for (var i = 0; i < Bodies; i++)
        {
            var body = new RigidBody2D { Position = new Vector2(i % 200 * 70 - 7000, 25 + i / 200 * 51), CanSleep = false };
            body.AddChild(new CollisionShape2D { Shape = square });
            scene2D.AddChild(body);
        }

        _tree2D.ChangeScene(scene2D);

        // Let both settle into resting contact.
        for (var i = 0; i < 120; i++)
        {
            _tree3D.Tick(_step);
            _tree2D.Tick(_step);
        }
    }

    private static SceneTree NewTree(IServer physics)
    {
        var servers = new ServerRegistry();
        servers.Register(physics);
        return new SceneTree(servers);
    }

    /// <summary>One frame with one physics step: 1 000 awake boxes in contact with the floor (3D).</summary>
    [Benchmark]
    public void Step1kRigidBodies3D() => _tree3D.Tick(_step);

    /// <summary>One frame with one physics step: 1 000 awake boxes in contact with the ground (2D).</summary>
    [Benchmark]
    public void Step1kRigidBodies2D() => _tree2D.Tick(_step);

    /// <summary>10 000 vertical raycasts over the 3D grid (about half hit a box).</summary>
    [Benchmark]
    public int Raycast10k3D()
    {
        var hits = 0;
        for (var i = 0; i < Rays; i++)
        {
            var x = i % 100 * 0.6f - 30;
            var z = i / 100 * 0.37f - 18;
            if (_query.RayCast(new Vector3(x, 5, z), new Vector3(x, -1, z), out _))
                hits++;
        }

        return hits;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        foreach (var tree in new[] { _tree3D, _tree2D })
        {
            if (tree is null)
                continue;
            tree.Shutdown();
            tree.Servers.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
