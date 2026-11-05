using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Paths (G13, docs/design/paths.md): closest-point queries on a ~10 km closed rail baked every 0.2 m (about 50 000
/// segments): the grid, the hinted query and the full scan they replace; and one tick of 1 000 followers.
/// </summary>
[MemoryDiagnoser]
public class PathBenchmarks
{
    private readonly Vector3[] _queries = new Vector3[64];
    private Curve3D _rail = null!;
    private float[] _hints = null!;
    private Vector3[] _baked = null!;
    private SceneTree _tree = null!;
    private uint _frame;

    [GlobalSetup]
    public void Setup()
    {
        var points = new Vector3[16];
        for (var i = 0; i < points.Length; i++)
        {
            var a = i * MathF.Tau / points.Length;
            points[i] = new Vector3(MathF.Cos(a) * 1600f, MathF.Sin(a * 3) * 30f, MathF.Sin(a) * 1600f);
        }

        _rail = new Curve3D { BakeInterval = 0.2f, UpVectorEnabled = true };
        _rail.SetSmoothPoints(points, closed: true);
        var length = _rail.GetBakedLength();
        _hints = new float[_queries.Length];
        for (var i = 0; i < _queries.Length; i++)
        {
            _hints[i] = length * i / _queries.Length;
            _queries[i] = _rail.SampleBaked(_hints[i] + 3f) + new Vector3(2, 1.5f, -1);
        }

        _baked = _rail.GetBakedPoints().ToArray();
        _rail.GetClosestOffset(Vector3.Zero); // builds the grid

        _tree = new SceneTree(new ServerRegistry());
        var scene = new Node3D { Name = "Scene" };
        _tree.ChangeScene(scene);
        var path = new Path3D { Name = "Rail", Curve = _rail };
        scene.AddChild(path);
        for (var i = 0; i < 1000; i++)
            path.AddChild(new PathFollow3D { Speed = 5 + i % 20, RotationMode = (PathRotationMode)(i % 5), Progress = i * 10f });
    }

    [Benchmark]
    public float ClosestGrid64()
    {
        var sum = 0f;
        foreach (var q in _queries)
            sum += _rail.GetClosestOffset(q);
        return sum;
    }

    [Benchmark]
    public float ClosestHinted64()
    {
        var sum = 0f;
        for (var i = 0; i < _queries.Length; i++)
            sum += _rail.GetClosestOffset(_queries[i], _hints[i], 20f);
        return sum;
    }

    /// <summary>What every query cost before the grid: the projection on each baked segment.</summary>
    [Benchmark]
    public float ClosestScan1()
    {
        var p = _queries[0];
        var best = float.PositiveInfinity;
        for (var i = 0; i < _baked.Length - 1; i++)
        {
            var a = _baked[i];
            var d = _baked[i + 1] - a;
            var t = Math.Clamp(Vector3.Dot(p - a, d) / d.LengthSquared(), 0f, 1f);
            best = MathF.Min(best, Vector3.DistanceSquared(a + d * t, p));
        }

        return best;
    }

    [Benchmark]
    public void Tick1000Followers() => _tree.Tick(new GameTime { DeltaTime = 1f / 60f, FrameCount = ++_frame });
}
