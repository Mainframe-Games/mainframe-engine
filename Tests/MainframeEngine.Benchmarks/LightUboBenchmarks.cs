using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>Lights UBO packing, done once per lit draw per frame (1200-byte std140 block).</summary>
[MemoryDiagnoser]
public class LightUboBenchmarks
{
    private readonly byte[] _ubo = new byte[LightEnvironment.UboSize];
    private readonly LightEnvironment _single = new();
    private readonly LightEnvironment _full = new();
    private Vector3 _camera = new(0, 5, 10);

    [GlobalSetup]
    public void Setup()
    {
        _single.AddLight(new DirectionalLight());

        for (var i = 0; i < LightEnvironment.MaxDirectional; i++)
            _full.AddLight(new DirectionalLight { Intensity = i });
        for (var i = 0; i < LightEnvironment.MaxPoint; i++)
            _full.AddLight(new PointLight { Position = new Vector3(i, 1, 0) });
        for (var i = 0; i < LightEnvironment.MaxSpot; i++)
            _full.AddLight(new SpotLight { Position = new Vector3(0, 1, i) });
    }

    [Benchmark(Baseline = true)]
    public byte PackSingleDirectional()
    {
        _single.WriteUbo(_ubo, _camera);
        return _ubo[0];
    }

    [Benchmark]
    public byte PackAllSlots()
    {
        _full.WriteUbo(_ubo, _camera);
        return _ubo[0];
    }
}
