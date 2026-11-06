using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Navigation avoidance (G3.7): one fixed step of RVO2's circle scenario — agents on a 400 px circle walking to the
/// opposite point and back for ever, through four polygon pillars — with 64 and 256 agents (ORCA, the neighbour grid,
/// obstacle edges, <see cref="NavigationAgent2D.VelocityComputed"/>). No paths: avoidance alone. 0 B.
/// </summary>
[MemoryDiagnoser]
public class AvoidanceBenchmarks : IDisposable
{
    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private SceneTree _tree64 = null!;
    private SceneTree _tree256 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _tree64 = Circle(64);
        _tree256 = Circle(256);
    }

    [Benchmark]
    public void Avoid64AgentsTick() => _tree64.Tick(_step);

    [Benchmark]
    public void Avoid256AgentsTick() => _tree256.Tick(_step);

    private SceneTree Circle(int count)
    {
        var servers = new ServerRegistry();
        servers.Register(new NavigationServer2D());
        var tree = new SceneTree(servers);
        var scene = new Node2D { Name = "Scene" };
        for (var i = 0; i < 4; i++)
        {
            var angle = i * MathF.Tau / 4f + 0.4f;
            scene.AddChild(new NavigationObstacle2D
            {
                Name = $"Pillar{i}",
                Position = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 120f,
                Vertices = [new(-20, -20), new(20, -20), new(20, 20), new(-20, 20)],
            });
        }

        for (var i = 0; i < count; i++)
        {
            var angle = i * MathF.Tau / count;
            var start = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 400f;
            var body = new CircleWalker { Name = $"Walker{i}", Position = start, A = start, B = -start };
            body.AddChild(new NavigationAgent2D { Name = "Agent", AvoidanceEnabled = true, Radius = 4f, MaxSpeed = 120f, NeighborDistance = 60f });
            scene.AddChild(body);
        }

        tree.ChangeScene(scene);
        for (var i = 0; i < 600; i++)
            tree.Tick(_step); // into the steady crossing traffic; buffers at their size
        return tree;
    }

    public void Dispose()
    {
        foreach (var tree in new[] { _tree64, _tree256 })
        {
            tree?.Shutdown();
            tree?.Servers.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // Walks to B, then back to A, for ever, by the safe velocity.
    private sealed class CircleWalker : Node2D
    {
        private NavigationAgent2D? _agent;
        private bool _toB = true;

        public Vector2 A { get; set; }
        public Vector2 B { get; set; }

        protected override void OnReady()
        {
            _agent = GetNode<NavigationAgent2D>("Agent");
            _agent.VelocityComputed += Move;
        }

        private void Move(Vector2 velocity) => GlobalPosition += velocity * (1f / 60f);

        protected override void OnPhysicsProcess(float delta)
        {
            var goal = _toB ? B : A;
            var to = goal - GlobalPosition;
            if (to.LengthSquared() < 25f)
                _toB = !_toB;
            _agent!.SetVelocity(to.LengthSquared() > 1e-4f ? Vector2.Normalize(to) * 120f : Vector2.Zero);
        }
    }
}
