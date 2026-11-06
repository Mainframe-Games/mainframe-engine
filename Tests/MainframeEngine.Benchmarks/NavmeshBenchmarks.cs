using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// 2D navigation meshes (G3.9) on a 2048 × 2048 px hall with 64 slanted pillars (the allocation gate's level): a funnel
/// query corner to corner, the map's rebuild of the mesh at a sync (a region setting changed), and the partition of the
/// outlines into convex polygons (<see cref="NavigationPolygon.MakePolygonsFromOutlines"/>, an edit-time cost).
/// Queries and rebuilds are 0 B; the partition allocates its result.
/// </summary>
[MemoryDiagnoser]
public class NavmeshBenchmarks : IDisposable
{
    private readonly NavigationPath2D _path = new(256);
    private SceneTree _tree = null!;
    private NavigationMap2D _map = null!;
    private NavigationRegion2D _region = null!;
    private NavigationPolygon _scratch = null!;
    private bool _flip;

    [GlobalSetup]
    public void Setup()
    {
        var servers = new ServerRegistry();
        servers.Register(new NavigationServer2D());
        _tree = new SceneTree(servers);
        var scene = new Node2D { Name = "Scene" };
        _region = new NavigationRegion2D { Name = "Hall", NavigationPolygon = Hall() };
        scene.AddChild(_region);
        _tree.ChangeScene(scene);
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f, FrameCount = 1 });
        _map = _region.Map!;
        _scratch = Hall();
        for (var i = 0; i < 4; i++)
        {
            QueryAcross();
            Rebuild();
        }
    }

    private static NavigationPolygon Hall()
    {
        var polygon = new NavigationPolygon();
        polygon.AddOutline([new(0, 0), new(2048, 0), new(2048, 2048), new(0, 2048)]);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++)
            {
                float x0 = 100 + x * 240 + y * 9, y0 = 100 + y * 240, x1 = 200 + x * 240, y1 = 220 + y * 240 - x * 4;
                polygon.AddOutline([new(x0, y0), new(x1, y0 + 6), new(x1 - 4, y1), new(x0 + 3, y1 - 5)]);
            }

        polygon.MakePolygonsFromOutlines();
        return polygon;
    }

    [Benchmark]
    public GridPathStatus QueryAcross() =>
        _map.QueryPath(new NavigationPathQuery2D(new Vector2(20, 20), new Vector2(2030, 2020)), _path);

    [Benchmark]
    public void Rebuild()
    {
        _flip = !_flip;
        _region.TravelCost = _flip ? 1.5f : 1f;
        _map.ForceSync();
    }

    [Benchmark]
    public void Partition() => _scratch.MakePolygonsFromOutlines();

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
        GC.SuppressFinalize(this);
    }
}
