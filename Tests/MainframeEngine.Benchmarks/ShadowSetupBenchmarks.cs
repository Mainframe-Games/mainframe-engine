using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// CPU side of the shadow pass, once per frame: cascade fitting (splits, sphere fit, texel snapping, caster pull-back)
/// and atlas packing. Recording and caster culling are measured by the render tests.
/// </summary>
[MemoryDiagnoser]
public class ShadowSetupBenchmarks
{
    private readonly ShadowPlanner _planner = new();
    private readonly LightEnvironment _sun = new();
    private readonly LightEnvironment _allTypes = new();
    private readonly PerspectiveCamera _camera = new()
    {
        Position = new Vector3(0, 3, 8),
        Forward = Vector3.Normalize(new Vector3(0, -0.3f, -1f)),
        AspectRatio = 16f / 9f,
        FieldOfView = 60f,
    };

    private readonly Aabb _casters = new(new Vector3(-50, 0, -50), new Vector3(50, 12, 50));
    private readonly int[] _requests = [1024, 2048, 512, 512, 1024, 1024, 256, 256, 128, 1024, 512];
    private readonly ShadowAtlasTile[] _tiles = new ShadowAtlasTile[11];
    private readonly ShadowAtlasAllocator _atlas = new(4096);
    private float _x;

    [GlobalSetup]
    public void Setup()
    {
        var direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f));
        _sun.AddLight(new DirectionalLight { Direction = direction });

        _allTypes.AddLight(new DirectionalLight { Direction = direction });
        _allTypes.AddLight(new DirectionalLight { Direction = Vector3.Normalize(new Vector3(0.8f, -1f, 0.3f)) });
        for (var i = 0; i < 3; i++)
            _allTypes.AddLight(new SpotLight { Position = new Vector3(i * 3f, 5, 0), Direction = -Vector3.UnitY, Range = 12f });
        for (var i = 0; i < 2; i++)
            _allTypes.AddLight(new PointLight { Position = new Vector3(i * 4f, 2, 1), Range = 8f });
    }

    // The camera moves a little every call, as in a game (the atlas stays packed: its requests do not change).
    private void Step()
    {
        _x += 0.01f;
        if (_x > 10f)
            _x = 0f;
        _camera.Position = new Vector3(_x, 3, 8);
    }

    [Benchmark(Baseline = true)]
    public int PlanSunCascades()
    {
        Step();
        _planner.Plan(_sun, _camera, _casters);
        return _planner.PassCount;
    }

    [Benchmark]
    public int PlanEveryLightType()
    {
        Step();
        _planner.Plan(_allTypes, _camera, _casters);
        foreach (var pass in _planner.Passes)
            _planner.SetHasCasters(pass.Index, true);
        _planner.ApplyCulling();
        return _planner.PassCount;
    }

    [Benchmark]
    public int PackAtlasElevenTiles() => _atlas.Pack(_requests, _tiles);
}
