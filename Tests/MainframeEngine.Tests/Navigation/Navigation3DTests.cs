using System.Numerics;

namespace MainframeEngine.Tests.Navigation;

/// <summary>
/// G3.11: <see cref="NavigationServer3D"/>, <see cref="NavigationGrid3D"/> (the XZ grid at a height, baked from 3D static
/// bodies) and <see cref="NavigationAgent3D"/> — sync, queries, heights, the bake's box, agents walking a
/// <see cref="CharacterBody3D"/> round a wall, the debug draw, and the allocation gate's 3D case (<b>0 bytes</b>).
/// </summary>
public sealed class Navigation3DTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly NavigationServer3D _navigation = new();
    private readonly Node3D _scene = new() { Name = "Scene" };

    public Navigation3DTests()
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

    private T Add<T>(T node) where T : Node
    {
        _scene.AddChild(node);
        return node;
    }

    private StaticBody3D AddBox(Vector3 centre, Vector3 size, string name = "Box")
    {
        var body = Add(new StaticBody3D { Name = name, Position = centre });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        return body;
    }

    // A 20 × 12 m grid of 1 m cells at the origin, a floor slab under it, a wall across x = 10 from z = 0 to 9.
    private NavigationGrid3D Level(float height = 0f)
    {
        AddBox(new Vector3(10, height - 0.5f, 6), new Vector3(30, 1, 20), "Floor");
        AddBox(new Vector3(10.5f, height + 1f, 4.5f), new Vector3(1, 2, 9), "Wall");
        var grid = Add(new NavigationGrid3D { Name = "Grid", Position = new Vector3(0, height, 0), Region = new Rect2I(0, 0, 20, 12) });
        Run(1);
        Assert.True(grid.Bake());
        Run(1);
        return grid;
    }

    [Fact]
    public void TheBakeMarksCellsUnderStaticBodiesButNotTheFloor()
    {
        var grid = Level();
        Assert.Equal(9, grid.Data!.SolidCount); // the wall's cells (10, 0..8); the floor is below AgentMaxClimb
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(10, 4)));
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(10, 9)));
        Assert.False(grid.IsBakeOutOfDate());

        var low = AddBox(new Vector3(3.5f, 0.1f, 3.5f), new Vector3(1, 0.2f, 1), "Step"); // under the climb height
        var high = AddBox(new Vector3(5.5f, 3f, 3.5f), new Vector3(1, 1, 1), "Shelf");     // above the agent
        Run(1);
        Assert.False(grid.IsBakeOutOfDate()); // neither is in the agent's height band
        _scene.GetNode<StaticBody3D>("Wall").Position = new Vector3(12.5f, 1f, 4.5f);
        Run(1);
        Assert.True(grid.IsBakeOutOfDate());
        Assert.True(grid.Bake());
        Run(1);
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(3, 3)));
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(5, 3)));
        grid.AgentRadius = 0.6f; // grows each box into the neighbouring cells
        Assert.True(grid.Bake());
        Run(1);
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(11, 4)));
        Assert.True(grid.Grid.IsPointSolid(new Vector2I(13, 4)));
        Assert.False(grid.Grid.IsPointSolid(new Vector2I(10, 4)));
        low.QueueFree();
        high.QueueFree();
    }

    [Fact]
    public void QueriesRunOnThePlaneAtTheGridsHeight()
    {
        var grid = Level(height: 2f);
        Assert.Same(grid, Assert.Single(Map.Regions));
        var path = new NavigationPath3D();
        var status = Map.QueryPath(new NavigationPathQuery3D(new Vector3(2.5f, 2f, 2.5f), new Vector3(17.5f, 2f, 2.5f)), path);
        Assert.Equal(GridPathStatus.Found, status);
        Assert.Same(grid, path.Region);
        Assert.Equal(new Vector3(2.5f, 2f, 2.5f), path.Points[0]);
        Assert.Equal(new Vector3(17.5f, 2f, 2.5f), path.Points[^1]);
        Assert.All(path.Points.ToArray(), p => Assert.Equal(2f, p.Y));
        Assert.Contains(path.Points.ToArray(), p => p.Z >= 9f); // round the end of the wall
        Assert.All(path.Owners.ToArray(), o => Assert.Same(grid, o));

        Assert.Equal(new Vector3(9.5f, 2f, 4.5f), Map.GetClosestPoint(new Vector3(10.4f, 5f, 4.5f))); // the wall's cell: the nearest open one
        Assert.Equal(GridPathStatus.InvalidPoint, Map.QueryPath(new NavigationPathQuery3D(new Vector3(-5, 0, 0), new Vector3(5, 0, 5)), path));
        Assert.Equal(new Vector2I(3, 7), grid.CellAt(new Vector3(3.2f, 0, 7.9f)));
        Assert.Equal(new Vector3(3.5f, 2f, 7.5f), grid.CellCenter(new Vector2I(3, 7)));
    }

    [Fact]
    public void EditsAndMovesApplyAtTheNextSync()
    {
        var grid = Level();
        var iteration = Map.IterationId;
        grid.SetCellSolid(new Vector2I(10, 9), true);
        Assert.False(grid.LiveGrid.IsPointSolid(new Vector2I(10, 9)));
        Run(1);
        Assert.True(grid.LiveGrid.IsPointSolid(new Vector2I(10, 9)));
        Assert.True(Map.IterationId > iteration);

        iteration = Map.IterationId;
        grid.Position = new Vector3(0, 1, 0);
        Run(1);
        Assert.True(Map.IterationId > iteration);
        Assert.Equal(1f, grid.PlacedHeight);
        iteration = Map.IterationId;
        Run(2);
        Assert.Equal(iteration, Map.IterationId); // nothing changed: no new iteration
    }

    private sealed class Walker3D : CharacterBody3D
    {
        public NavigationAgent3D Agent => GetNode<NavigationAgent3D>("Agent");
        public readonly List<string> Signals = [];

        protected override void OnReady()
        {
            Agent.PathChanged += () => Signals.Add("path");
            Agent.TargetReached += () => Signals.Add("target");
            Agent.NavigationFinished += () => Signals.Add("finished");
        }

        protected override void OnPhysicsProcess(float delta)
        {
            if (Agent.IsNavigationFinished())
            {
                Velocity = new Vector3(0, Velocity.Y - 9.8f * delta, 0);
                MoveAndSlide();
                return;
            }

            var to = Agent.GetNextPathPosition() - GlobalPosition;
            to.Y = 0f;
            var horizontal = to.LengthSquared() > 1e-6f ? Vector3.Normalize(to) * 4f : Vector3.Zero;
            Velocity = new Vector3(horizontal.X, Velocity.Y - 9.8f * delta, horizontal.Z);
            MoveAndSlide();
        }
    }

    [Fact]
    public void ACharacterWalksRoundTheWallToTheTarget()
    {
        Level();
        var body = new Walker3D { Name = "Walker", Position = new Vector3(2.5f, 1f, 2.5f) };
        body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.4f, Height = 2f } });
        body.AddChild(new NavigationAgent3D { Name = "Agent", PathHeightOffset = 1f, PathDesiredDistance = 0.5f, TargetDesiredDistance = 0.5f });
        Add(body);
        Run(2);
        body.Agent.TargetPosition = new Vector3(17.5f, 0f, 2.5f);
        var furthest = 0f;
        for (var i = 0; i < 60 * 20 && !body.Agent.IsTargetReached(); i++)
        {
            Run(1);
            furthest = MathF.Max(furthest, body.GlobalPosition.Z);
        }

        Assert.True(body.Agent.IsTargetReached(), $"at {body.GlobalPosition}");
        Assert.True(furthest > 8.5f, "went round the wall's end");
        Assert.Equal(["path", "target", "finished"], body.Signals);
        Assert.InRange(body.GlobalPosition.Y, 0.9f, 1.1f); // stayed on the floor
    }

    [Fact]
    public void AgentsRepathWhenACellIsBlocked()
    {
        var grid = Level();
        var walker = Add(new Node3D { Name = "Marker", Position = new Vector3(2.5f, 0, 10.5f) });
        var agent = new NavigationAgent3D { Name = "Agent" };
        walker.AddChild(agent);
        Run(1);
        agent.TargetPosition = new Vector3(17.5f, 0, 10.5f);
        agent.GetNextPathPosition();
        var changes = 0;
        agent.PathChanged += () => changes++;
        for (var z = 9; z < 12; z++)
            grid.SetCellSolid(new Vector2I(10, z), true); // the gap closes
        Run(1);
        agent.GetNextPathPosition();
        Assert.Equal(1, changes);
        Assert.Equal(GridPathStatus.Partial, agent.CurrentPath.Status);
    }

    [Fact]
    public void TheDebugDrawShowsGridsAndPaths()
    {
        Level();
        var marker = Add(new Node3D { Name = "Marker", Position = new Vector3(2.5f, 0, 2.5f) });
        var agent = new NavigationAgent3D { Name = "Agent", DebugEnabled = true };
        marker.AddChild(agent);
        Run(1);
        agent.TargetPosition = new Vector3(17.5f, 0, 2.5f);
        agent.GetNextPathPosition();
        var lines = _tree.Root.DebugLines;
        lines.Clear();
        Run(1);
        var agentOnly = lines.LineCount;
        Assert.True(agentOnly > 3);
        _navigation.DebugDrawEnabled = true;
        lines.Clear();
        Run(1);
        Assert.True(lines.LineCount >= agentOnly + 4 + 9 * 6, $"{lines.LineCount} lines"); // the outline, each solid cell's outline and cross
    }

    [Fact]
    public void SixtyFourAgentsAllocateNothing()
    {
        _navigation.DebugDrawEnabled = true;
        var grid = Level();
        var markers = new Node3D[64];
        var agents = new NavigationAgent3D[64];
        for (var i = 0; i < markers.Length; i++)
        {
            markers[i] = Add(new Node3D { Name = $"Walker{i}", Position = new Vector3(0.5f + i % 8 * 0.5f, 0, 0.5f + i / 8 * 1.4f) });
            agents[i] = new NavigationAgent3D { Name = "Agent", TargetRepathDistance = 1f };
            markers[i].AddChild(agents[i]);
        }

        Run(1);
        var tick = 0;
        void Tick()
        {
            for (var i = 0; i < agents.Length; i++)
            {
                var angle = (tick % 300) / 300f * MathF.Tau + i;
                agents[i].TargetPosition = new Vector3(10 + MathF.Cos(angle) * (4f + i * 0.08f), 0, 6 + MathF.Sin(angle) * 4f);
                if (agents[i].IsNavigationFinished())
                    continue;
                var next = agents[i].GetNextPathPosition();
                var to = next - markers[i].Position;
                markers[i].Position += to.LengthSquared() > 0.01f ? Vector3.Normalize(to) * 0.1f : to;
            }

            if (tick % 30 == 0)
                grid.SetCellSolid(new Vector2I(15, 6), tick / 30 % 2 == 0);
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
        Assert.Contains(agents, a => a.CurrentPath.Count > 2);
    }
}
