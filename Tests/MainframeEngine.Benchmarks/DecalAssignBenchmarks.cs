using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// G6.5: the CPU side of forward decals for a view: selecting 32 of 64 decals (frustum, distance, nearest) and giving
/// 10 000 instance bounds their decal masks, as <c>MeshRenderer.Prepare</c> does every frame.
/// </summary>
[MemoryDiagnoser]
public class DecalAssignBenchmarks
{
    private const int Instances = 10_000;

    private readonly Aabb[] _bounds = new Aabb[Instances];
    private readonly DecalSelection _selection = new();
    private List<Decal> _decals = [];
    private Frustum _frustum;
    private readonly Vector3 _camera = new(0, 30, 60);

    [GlobalSetup]
    public void Setup()
    {
        var root = new Node3D { Name = "Root" };
        var texture = Texture2D.FromPixels(1, 1, [255, 255, 255, 255]);
        for (var i = 0; i < 64; i++)
        {
            var decal = new Decal { TextureAlbedo = texture, Size = new Vector3(6, 4, 6), Position = new Vector3(i % 8 * 12 - 48, 0, i / 8 * 12 - 48) };
            root.AddChild(decal);
            _decals.Add(decal);
        }

        var unit = new Aabb(new Vector3(-0.5f), new Vector3(0.5f));
        for (var i = 0; i < Instances; i++)
            _bounds[i] = unit.Transform(Matrix4x4.CreateTranslation(i % 100 - 50, 0, i / 100 - 50));
        var view = Matrix4x4.CreateLookAt(_camera, Vector3.Zero, Vector3.UnitY);
        _frustum = new Frustum(view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 500f));
        Assign32x10k();
    }

    /// <summary>Select the view's decals, then one mask per instance.</summary>
    [Benchmark]
    public uint Assign32x10k()
    {
        _selection.Select(_decals, _frustum, _camera, null);
        uint any = 0;
        for (var i = 0; i < _bounds.Length; i++)
            any |= _selection.MaskFor(_bounds[i]);
        return any;
    }
}
