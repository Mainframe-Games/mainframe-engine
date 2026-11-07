using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.12: 3D navigation meshes baked with Recast (DotRecast) — obstacles carved by the agent radius, ramps onto platforms,
/// mesh and collision sources, moved regions, islands and partial paths, deterministic bakes, a character walking up a
/// ramp, and the allocation gate's navmesh case (<b>0 bytes</b>).
/// </summary>
public sealed class NavigationMesh3DTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly NavigationServer3D _navigation = new();
    private readonly Node3D _scene = new() { Name = "Scene" };

    public NavigationMesh3DTests()
    {
        var servers = new ServerRegistry();
        servers.Register(new PhysicsServer3D(new PhysicsSettings3D { MultiThreaded = false }));
        servers.Register(_navigation);
        _tree = new SceneTree(servers);
        _tree.ChangeScene(_scene);
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private NavigationMap3D Map => _navigation.GetMap(_tree.Root.World3D);

    private void Run(int frames)
    {
        var time = new GameTime { DeltaTime = 1f / 60f };
        for (var i = 0; i < frames; i++)
            _tree.Tick(time);
    }

    private static StaticBody3D Box(Node parent, Vector3 centre, Vector3 size, string name, Vector3 rotationDegrees = default)
    {
        var body = new StaticBody3D { Name = name, Position = centre, RotationDegrees = rotationDegrees };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        parent.AddChild(body);
        return body;
    }

    // A 20 × 20 m floor with a 2 m pillar in the middle, a platform 1.5 m up at x 5–9, and a ramp onto it from x = 1.
    private NavigationRegion3D Level(bool ramp = true)
    {
        var region = new NavigationRegion3D { Name = "Region", NavigationMesh = new NavigationMesh { AgentRadius = 0.4f, CellSize = 0.2f, CellHeight = 0.1f } };
        _scene.AddChild(region);
        Box(region, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "Floor");
        Box(region, new Vector3(0, 1, 0), new Vector3(2, 2, 2), "Pillar");
        Box(region, new Vector3(7, 0.75f, 6), new Vector3(4, 1.5f, 4), "Platform");
        if (ramp)
            Box(region, new Vector3(3.2f, 0.75f, 6), new Vector3(4.5f, 0.2f, 2), "Ramp", new Vector3(0, 0, 20f));
        Run(1);
        Assert.True(region.Bake());
        Run(1);
        return region;
    }

    private NavigationPath3D Query(Vector3 from, Vector3 to, bool partial = true)
    {
        var path = new NavigationPath3D();
        Map.QueryPath(new NavigationPathQuery3D(from, to, AllowPartialPath: partial), path);
        return path;
    }

    [Fact]
    public void ABakeCarvesObstaclesByTheAgentRadius()
    {
        var region = Level();
        var mesh = region.NavigationMesh!;
        Assert.True(mesh.IsBaked);
        Assert.True(mesh.PolygonCount > 4);
        Assert.Equal(mesh.Vertices.Length, mesh.Vertices.Distinct().Count());
        Assert.Same(region, Assert.Single(Map.MeshRegions));

        // The pillar (±1 m) plus the radius (0.4 m) is not walkable; the floor beside it is.
        var inside = Map.GetClosestPoint(new Vector3(0, 0, 0));
        Assert.True(MathF.Max(MathF.Abs(inside.X), MathF.Abs(inside.Z)) >= 1.3f, $"{inside}");
        var open = Map.GetClosestPoint(new Vector3(-4, 0.5f, -4));
        Assert.True(Vector2.Distance(new Vector2(open.X, open.Z), new Vector2(-4, -4)) < 0.05f);
        Assert.InRange(open.Y, -0.1f, 0.3f);

        var path = Query(new Vector3(-6, 0, 0), new Vector3(6, 0, 0));
        Assert.Equal(GridPathStatus.Found, path.Status);
        Assert.Contains(path.Points.ToArray(), p => MathF.Abs(p.Z) >= 1.3f); // round the pillar
        Assert.All(path.Owners.ToArray(), o => Assert.Same(region, o));
        Assert.True(region.LastBakeMilliseconds > 0);
    }

    [Fact]
    public void ARampLeadsOntoThePlatform()
    {
        Level();
        var path = Query(new Vector3(-6, 0, 6), new Vector3(7, 1.5f, 6));
        Assert.Equal(GridPathStatus.Found, path.Status);
        Assert.InRange(path.Points[^1].Y, 1.3f, 1.7f);

        // Without the ramp the platform is an island: the path stops at its foot.
        foreach (var child in _scene.Children.ToArray())
            child.Free();
        Level(ramp: false);
        var partial = Query(new Vector3(-6, 0, 6), new Vector3(7, 1.5f, 6));
        Assert.Equal(GridPathStatus.Partial, partial.Status);
        Assert.True(partial.Points[^1].Y < 0.5f);
        Assert.Equal(GridPathStatus.NotFound, Query(new Vector3(-6, 0, 6), new Vector3(7, 1.5f, 6), partial: false).Status);
    }

    [Fact]
    public void FunnelPathsAreShortAndEdgeCrossingsAddPoints()
    {
        Level();
        var funnel = Query(new Vector3(-6, 0, -6), new Vector3(6, 0, -6));
        Assert.Equal(2, funnel.Count); // a straight line across open floor
        var crossings = new NavigationPath3D();
        Map.QueryPath(new NavigationPathQuery3D(new Vector3(-6, 0, -6), new Vector3(6, 0, -6),
            Postprocessing: NavigationPathPostprocessing.EdgeCentered), crossings);
        Assert.True(crossings.Count >= funnel.Count);
    }

    [Fact]
    public void MeshesOrCollisionAreParsedAsTheSettingsSay()
    {
        var region = new NavigationRegion3D { Name = "Region", NavigationMesh = new NavigationMesh() };
        _scene.AddChild(region);
        region.AddChild(new MeshInstance3D { Name = "Ground", Position = new Vector3(0, -0.5f, 0), Mesh = new BoxMesh { Size = new Vector3(10, 1, 10) } });
        Run(1);
        Assert.True(region.Bake()); // Both: the visible mesh
        region.NavigationMesh!.ParsedGeometryType = NavigationParsedGeometryType.StaticColliders;
        Assert.Null(region.BakeData(out var error));
        Assert.Contains("no source geometry", error);
        region.NavigationMesh.ParsedGeometryType = NavigationParsedGeometryType.MeshInstances;
        Assert.NotNull(region.BakeData(out _));
    }

    [Fact]
    public void MovedRegionsCarryTheirMesh()
    {
        var region = Level();
        var before = Query(new Vector3(-6, 0, -6), new Vector3(-2, 0, -6));
        var iteration = Map.IterationId;
        region.Position = new Vector3(100, 0, 0);
        Run(1);
        Assert.True(Map.IterationId > iteration);
        var after = Query(new Vector3(94, 0, -6), new Vector3(98, 0, -6));
        Assert.Equal(GridPathStatus.Found, after.Status);
        Assert.Equal(before.Points[^1] + new Vector3(100, 0, 0), after.Points[^1]);

        region.Enabled = false;
        Run(1);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(new Vector3(94, 0, -6), new Vector3(98, 0, -6)).Status);
    }

    [Fact]
    public void BakesAreDeterministicAndClearable()
    {
        var region = Level();
        var data = region.NavigationMesh!.DetourData;
        var second = region.BakeData(out _)!.Value;
        Assert.Equal(data, second.DetourData);
        Assert.Equal(region.NavigationMesh.Vertices, second.Vertices);
        region.NavigationMesh.Clear();
        Run(1);
        Assert.False(region.NavigationMesh.IsBaked);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(new Vector3(-6, 0, -6), new Vector3(6, 0, -6)).Status);
    }

    [Fact]
    public void CorruptDetourDataIsIgnoredWithAWarning()
    {
        var region = Level();
        region.NavigationMesh!.DetourData = "dtmd1:AAAA";
        Run(1);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(new Vector3(-6, 0, -6), new Vector3(6, 0, -6)).Status);
        region.NavigationMesh.DetourData = "nonsense";
        Run(1);
        Assert.Equal(GridPathStatus.InvalidPoint, Query(new Vector3(-6, 0, -6), new Vector3(6, 0, -6)).Status);
    }

    private sealed class Climber : CharacterBody3D
    {
        public NavigationAgent3D Agent => GetNode<NavigationAgent3D>("Agent");

        protected override void OnPhysicsProcess(float delta)
        {
            var horizontal = Vector3.Zero;
            if (!Agent.IsNavigationFinished())
            {
                var to = Agent.GetNextPathPosition() - GlobalPosition;
                to.Y = 0f;
                if (to.LengthSquared() > 1e-6f)
                    horizontal = Vector3.Normalize(to) * 3f;
            }

            Velocity = new Vector3(horizontal.X, Velocity.Y - 9.8f * delta, horizontal.Z);
            MoveAndSlide();
        }
    }

    [Fact]
    public void ACharacterWalksUpTheRampOntoThePlatform()
    {
        Level();
        var body = new Climber { Name = "Climber", Position = new Vector3(-6, 0.9f, 6) };
        body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f } });
        body.AddChild(new NavigationAgent3D { Name = "Agent", PathHeightOffset = 0.9f, PathDesiredDistance = 0.5f, TargetDesiredDistance = 0.4f });
        _scene.AddChild(body);
        Run(2);
        body.Agent.TargetPosition = new Vector3(7, 1.5f, 6);
        for (var i = 0; i < 60 * 15 && !body.Agent.IsTargetReached(); i++)
            Run(1);
        Assert.True(body.Agent.IsTargetReached(), $"at {body.GlobalPosition}");
        Assert.InRange(body.GlobalPosition.Y, 2.2f, 2.6f); // the capsule's centre 0.9 m over the platform's top
    }

    [Fact]
    public void SixtyFourAgentsOnANavmeshAllocateNothing()
    {
        _navigation.DebugDrawEnabled = true;
        var region = Level();
        var markers = new Node3D[64];
        var agents = new NavigationAgent3D[64];
        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = new Node3D { Name = $"Walker{i}", Position = new Vector3(-8 + i % 8 * 0.6f, 0, -8 + i / 8 * 0.6f) };
            agents[i] = new NavigationAgent3D { Name = "Agent", TargetRepathDistance = 1f, PathDesiredDistance = 0.3f };
            markers[i].AddChild(agents[i]);
            _scene.AddChild(markers[i]);
        }

        Run(1);
        var tick = 0;
        void Tick()
        {
            for (var i = 0; i < agents.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                agents[i].TargetPosition = new Vector3(MathF.Cos(angle) * (4f + i * 0.05f), 0, MathF.Sin(angle) * 4f - 2f);
                if (agents[i].IsNavigationFinished())
                    continue;
                var to = agents[i].GetNextPathPosition() - markers[i].Position;
                markers[i].Position += to.LengthSquared() > 0.01f ? Vector3.Normalize(to) * 0.1f : to;
            }

            if (tick % 60 == 0)
                region.Position = new Vector3(0, 0, tick / 60 % 2 == 0 ? 0f : 0.001f); // a move: the map changes, agents repath
            Run(1);
            _tree.Root.DebugLines.Clear();
            tick++;
        }

        for (var i = 0; i < 600; i++)
            Tick();
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 300; i++)
                Tick();
        });
        Assert.Equal(0, allocated);
        Assert.Contains(agents, a => a.CurrentPath.Count >= 2);
    }
}
