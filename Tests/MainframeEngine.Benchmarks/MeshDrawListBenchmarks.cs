using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// The CPU side of the mesh batcher (M3) for 10 000 instances, without a GPU: the per-instance work of
/// <c>MeshRenderer.Prepare</c> (visibility, model matrix, mirror test, bounds transform, frustum cull, sort key)
/// into a <see cref="DrawList{T}"/> and its sort; transparent keys (back to front); and writing the sorted
/// instance data (model matrix + object id) as the frame's instance buffer would.
/// </summary>
[MemoryDiagnoser]
public class MeshDrawListBenchmarks
{
    private const int Count = 10_000;

    private readonly MeshInstance3D[] _nodes = new MeshInstance3D[Count];
    private readonly DrawList<int> _opaque = new(Count);
    private readonly DrawList<int> _transparent = new(Count);
    private readonly MeshInstanceData[] _instances = new MeshInstanceData[Count];
    private Frustum _frustum;
    private Vector3 _cameraPosition;
    private Vector3 _forward;
    private readonly Aabb _bounds = new BoxMesh().Bounds;

    [GlobalSetup]
    public void Setup()
    {
        var root = new Node3D { Name = "Root" };
        var mesh = new BoxMesh();
        var materials = new[] { new StandardMaterial3D(), new StandardMaterial3D(), new StandardMaterial3D(), new StandardMaterial3D() };
        for (var i = 0; i < Count; i++)
        {
            _nodes[i] = new MeshInstance3D
            {
                Mesh = mesh,
                MaterialOverride = materials[i % 4],
                Position = new Vector3(i % 100 - 50, 0, i / 100 - 50),
                RotationDegrees = new Vector3(0, i % 90, 0),
            };
            root.AddChild(_nodes[i]);
        }

        _cameraPosition = new Vector3(0, 30, 60);
        var view = Matrix4x4.CreateLookAt(_cameraPosition, Vector3.Zero, Vector3.UnitY);
        _frustum = new Frustum(view * Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 500f));
        _forward = -new Vector3(view.M13, view.M23, view.M33);
        BuildAndSortOpaque10k();
        BuildAndSortTransparent10k();
    }

    /// <summary>Cull + key + add for every instance, then sort (pipeline → material → mesh).</summary>
    [Benchmark]
    public int BuildAndSortOpaque10k()
    {
        _opaque.Clear();
        for (var i = 0; i < _nodes.Length; i++)
        {
            var node = _nodes[i];
            if (!node.IsVisibleInTree())
                continue;
            var model = node.ModelMatrix;
            var mirrored = MeshRenderer.Determinant3(model) < 0f;
            if (!_frustum.Intersects(_bounds.Transform(model)))
                continue;
            _opaque.Add(DrawSortKey.Opaque(mirrored ? 2 : 1, i & 3, 1, 0), i);
        }

        _opaque.Sort();
        return _opaque.Count;
    }

    /// <summary>The same with transparent keys (view depth, back to front).</summary>
    [Benchmark]
    public int BuildAndSortTransparent10k()
    {
        _transparent.Clear();
        for (var i = 0; i < _nodes.Length; i++)
        {
            var bounds = _bounds.Transform(_nodes[i].ModelMatrix);
            if (!_frustum.Intersects(bounds))
                continue;
            var depth = Vector3.Dot(bounds.Center - _cameraPosition, _forward);
            _transparent.Add(DrawSortKey.Transparent(0, depth, 1, i & 3), i);
        }

        _transparent.Sort();
        return _transparent.Count;
    }

    /// <summary>Writes the sorted list's instance data (what each frame copies into the instance buffer).</summary>
    [Benchmark]
    public int WriteInstances10k()
    {
        var count = _opaque.Count;
        for (var k = 0; k < count; k++)
        {
            var node = _nodes[_opaque[k]];
            _instances[k] = new MeshInstanceData(node.ModelMatrix, node.ObjectId);
        }

        return count;
    }
}
