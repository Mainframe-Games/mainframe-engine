using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Scene tree costs at scale (M2): one frame over 10 000 processing nodes, recomputing a dirty 10 000-node
/// transform subtree, a 6-deep path lookup, and saving + loading a 1 000-node scene.
/// </summary>
[MemoryDiagnoser]
public class SceneTreeBenchmarks : IDisposable
{
    private SceneTree _tree = null!;
    private Node3D _transformRoot = null!;
    private readonly List<Node3D> _leaves = [];
    private Node _lookupRoot = null!;
    private Node3D _sceneRoot = null!;
    private readonly NodePath _path = "N0_10/N1_10/N2_10/N3_10/N4_10/N5_10";
    private GameTime _time = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private float _offset;

    [GlobalSetup]
    public void Setup()
    {
        // 10 000 processing nodes (100 branches × 99 leaves + branches) in one tree.
        _tree = new SceneTree();
        var scene = new Node { Name = "Scene" };
        for (var b = 0; b < 100; b++)
        {
            var branch = new BenchCounterNode { Name = $"B{b}" };
            scene.AddChild(branch);
            for (var l = 0; l < 99; l++)
                branch.AddChild(new BenchCounterNode());
        }

        _tree.ChangeScene(scene);
        for (var i = 0; i < 10; i++)
            _tree.Tick(_time);

        // 10 000 Node3D: 100 children of the root, each with 99 children.
        _transformRoot = new Node3D { Name = "Root" };
        for (var b = 0; b < 100; b++)
        {
            var branch = new Node3D { Position = new Vector3(b, 0, 0), RotationDegrees = new Vector3(0, b, 0) };
            _transformRoot.AddChild(branch);
            for (var l = 0; l < 99; l++)
            {
                var leaf = new Node3D { Position = new Vector3(0, l, 0), Scale = new Vector3(1.01f) };
                branch.AddChild(leaf);
                _leaves.Add(leaf);
            }
        }

        // Path lookup: 6 levels, 20 siblings each (the name index kicks in above 8 children).
        _lookupRoot = new Node { Name = "Root" };
        var current = _lookupRoot;
        for (var depth = 0; depth < 6; depth++)
        {
            for (var sibling = 0; sibling < 20; sibling++)
                current.AddChild(new Node { Name = $"N{depth}_{sibling}" });
            current = current.GetChild(10);
        }

        // 1 000-node scene: 10 branches × 99 transformed leaves, all owned by the root.
        _sceneRoot = new Node3D { Name = "Level" };
        for (var b = 0; b < 10; b++)
        {
            var branch = new Node3D { Name = $"Group{b}", Position = new Vector3(b, 0, 0) };
            _sceneRoot.AddChild(branch);
            branch.Owner = _sceneRoot;
            for (var l = 0; l < 99; l++)
            {
                var leaf = new Node3D { Name = $"Item{l}", Position = new Vector3(l, b, 0), RotationDegrees = new Vector3(0, l, 0) };
                branch.AddChild(leaf);
                leaf.Owner = _sceneRoot;
            }
        }
    }

    /// <summary>One full tick (physics step + process + flush + sync) over 10 000 processing nodes.</summary>
    [Benchmark]
    public void ProcessTick10k() => _tree.Tick(_time);

    /// <summary>Moving the root of a 10 000-node 3D tree, then reading every leaf's model matrix.</summary>
    [Benchmark]
    public float TransformPropagationDirtySubtree10k()
    {
        _offset += 0.001f;
        _transformRoot.Position = new Vector3(_offset, 0, 0);
        var sum = 0f;
        foreach (var leaf in _leaves)
            sum += leaf.ModelMatrix.M41;
        return sum;
    }

    /// <summary>A 6-level relative <see cref="NodePath"/> lookup (allocation-free).</summary>
    [Benchmark]
    public Node? GetNodePathLookup() => _lookupRoot.GetNodeOrNull(_path);

    /// <summary>Serialize a 1 000-node scene to JSON, parse it and instantiate it (then free the copy).</summary>
    [Benchmark]
    public int SceneSaveLoadRoundTrip1k()
    {
        var json = SceneSaver.ToJson(_sceneRoot);
        var copy = PackedScene.Parse(json).Instantiate();
        var count = copy.ChildCount;
        copy.Free();
        return count;
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _transformRoot.Free();
        _lookupRoot.Free();
        _sceneRoot.Free();
        GC.SuppressFinalize(this);
    }
}

/// <summary>A node whose process and physics callbacks only count.</summary>
public sealed class BenchCounterNode : Node
{
    public int Count { get; private set; }

    protected override void OnProcess(in GameTime gameTime) => Count++;

    protected override void OnPhysicsProcess(float delta) => Count++;
}
