using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Navigation (G3a): one corner-to-corner query on a 256 × 256 grid — an open field and a maze of walls with gaps — with
/// A* and jump point search, 8-way (no corner cutting, octile) and 4-way (Manhattan); and one fixed step of a tree whose
/// 64 agents all repath (a door toggles every step) on a 128 × 128 maze. All 0 B.
/// </summary>
[MemoryDiagnoser]
public class NavigationBenchmarks : IDisposable
{
    private const int Size = 256;
    private readonly Vector2I[] _path = new Vector2I[4096];
    private AStarGrid2D _open = null!;
    private AStarGrid2D _maze = null!;
    private SceneTree _tree = null!;
    private NavigationGrid2D _door = null!;
    private readonly GameTime _step = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private bool _closed;

    [GlobalSetup]
    public void Setup()
    {
        _open = new AStarGrid2D { Region = new Rect2I(0, 0, Size, Size) };
        _open.Update();
        _maze = new AStarGrid2D { Region = new Rect2I(0, 0, Size, Size) };
        _maze.Update();
        // Walls every 8 columns and rows, each with a gap at a pseudo-random place: long, winding paths.
        for (var i = 8; i < Size; i += 8)
        {
            var gap = i * 37 % (Size - 8) + 4;
            _maze.FillSolidRegion(new Rect2I(i, 0, 1, Size));
            _maze.FillSolidRegion(new Rect2I(i, gap, 1, 3), false);
            var gap2 = i * 53 % (Size - 8) + 4;
            _maze.FillSolidRegion(new Rect2I(0, i, Size, 1));
            _maze.FillSolidRegion(new Rect2I(gap2, i, 3, 1), false);
        }

        for (var i = 8; i < Size; i += 8)
            for (var j = 4; j < Size; j += 8)
                _maze.SetPointSolid(new Vector2I(i, j), false); // more openings so most rooms connect

        SetupAgents();
    }

    private static void Configure(AStarGrid2D grid, bool jumping, bool fourWay)
    {
        grid.JumpingEnabled = jumping;
        grid.DiagonalMode = fourWay ? GridDiagonalMode.Never : GridDiagonalMode.OnlyIfNoObstacles;
        grid.DefaultComputeHeuristic = grid.DefaultEstimateHeuristic = fourWay ? GridHeuristic.Manhattan : GridHeuristic.Octile;
    }

    private int Query(AStarGrid2D grid, bool jumping, bool fourWay)
    {
        Configure(grid, jumping, fourWay);
        grid.GetIdPath(new Vector2I(1, 1), new Vector2I(Size - 2, Size - 2), _path, out var count, allowPartialPath: true);
        return count;
    }

    [Benchmark]
    public int OpenField256AStar8Way() => Query(_open, false, false);

    [Benchmark]
    public int OpenField256Jps8Way() => Query(_open, true, false);

    [Benchmark]
    public int OpenField256AStar4Way() => Query(_open, false, true);

    [Benchmark]
    public int Maze256AStar8Way() => Query(_maze, false, false);

    [Benchmark]
    public int Maze256Jps8Way() => Query(_maze, true, false);

    [Benchmark]
    public int Maze256AStar4Way() => Query(_maze, false, true);

    [Benchmark]
    public int Maze256Jps4Way() => Query(_maze, true, true);

    /// <summary>One fixed step in which all 64 agents repath (a door toggles): 64 queries, path following, signals.</summary>
    [Benchmark]
    public void Repath64AgentsTick()
    {
        _closed = !_closed;
        for (var y = 0; y < 4; y++)
            _door.SetCellSolid(new Vector2I(64, 60 + y), _closed);
        _tree.Tick(_step);
    }

    private void SetupAgents()
    {
        var servers = new ServerRegistry();
        servers.Register(new NavigationServer2D());
        _tree = new SceneTree(servers);
        var scene = new Node2D { Name = "Scene" };
        _door = new NavigationGrid2D { Name = "Grid", Region = new Rect2I(0, 0, 128, 128), CellSize = new Vector2(16) };
        scene.AddChild(_door);
        for (var x = 16; x < 128; x += 16)
            for (var y = 0; y < 128; y++)
                if (y % 64 is not (8 or 9 or 40 or 41))
                    _door.SetCellSolid(new Vector2I(x, (y + x) % 128), true);
        for (var i = 0; i < 64; i++)
        {
            var walker = new BenchWalker { Name = $"Walker{i}", Position = new Vector2(8 + i % 8 * 32, 8 + i / 8 * 250) };
            var agent = new NavigationAgent2D { Name = "Agent" };
            walker.AddChild(agent);
            scene.AddChild(walker);
            agent.TargetPosition = new Vector2(2040 - i % 8 * 32, 2040 - i / 8 * 250);
        }

        _tree.ChangeScene(scene);
        for (var i = 0; i < 60; i++)
            Repath64AgentsTick();
    }

    public void Dispose()
    {
        _tree?.Shutdown();
        _tree?.Servers.Dispose();
        GC.SuppressFinalize(this);
    }

    // Walks towards the next path position without physics (the agents' cost only). It never arrives (speed 0), so
    // every toggle of the door repaths every agent.
    private sealed class BenchWalker : Node2D
    {
        private NavigationAgent2D? _agent;

        protected override void OnReady() => _agent = GetNode<NavigationAgent2D>("Agent");

        protected override void OnPhysicsProcess(float delta)
        {
            if (_agent is { } agent && !agent.IsNavigationFinished())
                agent.GetNextPathPosition();
        }
    }
}
