using MainframeEngine.Serialization;
using MainframeEngine.Trees;

namespace MainframeEngine.Editor.Tests;

/// <summary>G8b.7: Bake Variants — N seeds of a tree as meshes, tree scenes (with or without a trunk collider) and a scatter type.</summary>
[Collection(nameof(SerialEditor))]
public sealed class TreeVariantsTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public TreeVariantsTests()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(_editor.Directory, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_editor.Directory);
    }

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        ResourceLoader.ClearCache();
        _editor.Dispose();
    }

    private EditorWorkspace W => _editor.Workspace;

    private static Tree3DInspector Inspector => (Tree3DInspector)CustomInspectors.Find(typeof(Tree3D))!;

    private Tree3D AddTree(int seed = 321)
    {
        var tree = (Tree3D)TypeRegistry.CreateNode(nameof(Tree3D));
        tree.Name = "Tree";
        NewNodeDefaults.Apply(tree);
        tree.Seed = seed;
        _editor.Scene.AddNode(tree, _editor.Scene.Root);
        _editor.Tick(2);
        _editor.Scene.Selection.Set(tree);
        _editor.Tick();
        return tree;
    }

    private string Full(string projectPath) => Path.Combine(_editor.Directory, projectPath);

    [Fact]
    public void BakeVariantsWritesAMeshAndAColliderSceneEachAndATypeListingThem()
    {
        var tree = AddTree();
        var inspector = Inspector;
        inspector.CreateCollider = true;
        var header = inspector.GetHeaderRml(tree)!;
        Assert.Contains("Bake Variants — 4 seeds into Assets/Trees/oak_medium_realistic", header, StringComparison.Ordinal);
        Assert.Contains("Create Collider — on", header, StringComparison.Ordinal);

        W.Inspector.RunHeaderAction(Tree3DInspector.VariantsInput + "=3");
        Assert.Equal(3, inspector.VariantCount);
        var history = _editor.Scene.History.Position;
        var result = inspector.BakeVariants(tree)!;
        Assert.Equal(history, _editor.Scene.History.Position); // files only: the scene is unchanged
        Assert.Equal("Assets/Trees/oak_medium_realistic", result.Folder);
        Assert.Equal([321, 322, 323], result.Seeds);
        Assert.Equal("Assets/Trees/oak_medium_realistic/oak_medium_realistic_v1.mfres", result.Meshes[1]);
        Assert.Equal("Assets/Trees/oak_medium_realistic/oak_medium_realistic_v1.mfscene", result.Scenes[1]);
        Assert.All(result.Meshes.Concat(result.Scenes).Append(result.TypePath), p => Assert.True(File.Exists(Full(p)), p));
        Assert.Contains("3 variant scene(s) with colliders and oak_medium_realistic.mfres", inspector.GetHeaderRml(tree)!, StringComparison.Ordinal);

        ResourceLoader.ClearCache();
        var hashes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            var mesh = ResourceLoader.Load<TreeMesh>(result.Meshes[i]);
            Assert.Equal(321 + i, mesh.Seed);
            Assert.True(mesh.Lods.Length > 0);
            Assert.True(hashes.Add(mesh.BakeHash)); // every seed is another tree

            var root = ResourceLoader.Load<PackedScene>(result.Scenes[i]).Instantiate();
            var body = Assert.IsType<StaticBody3D>(root);
            Assert.Equal($"oak_medium_realistic_v{i}", body.Name);
            var baked = Assert.Single(body.Children.OfType<Tree3D>());
            Assert.False(baked.Collision); // the body collides, not an internal trunk
            Assert.Equal(result.Meshes[i], baked.BakedMesh!.ResourcePath);
            var shape = Assert.Single(body.Children.OfType<CollisionShape3D>());
            var capsule = Assert.IsType<CapsuleShape3D>(shape.Shape);
            Assert.Equal(mesh.TrunkRadius, capsule.Radius, 4);
            Assert.Equal(MathF.Max(mesh.TrunkHeight, 2f * mesh.TrunkRadius), capsule.Height, 4);
            Assert.Equal(capsule.Height * 0.5f, shape.Position.Y, 4);
            root.Free();
        }

        var type = ResourceLoader.Load<ScatterObjectType>(result.TypePath);
        Assert.Equal("oak_medium_realistic", type.Name);
        Assert.Equal(result.Scenes, type.Scenes.Select(s => s.ResourcePath));
        Assert.True(type.SpacingMeters >= 2f);
        Assert.True(type.SinkMeters > 0f);
    }

    [Fact]
    public void ARerunOverwritesTheVariantsAndKeepsTheTypesPlacementSettings()
    {
        var tree = AddTree();
        var first = TreeVariants.Bake(tree, 2, collider: true);
        var type = ResourceLoader.Load<ScatterObjectType>(first.TypePath);
        type.SpacingMeters = 7f;
        type.SlopeMaxDegrees = 20f;
        ResourceSaver.Save(type, first.TypePath);
        var uids = first.Scenes.Select(p => AssetDatabase.Current.GetUid(p)).ToArray();

        ResourceLoader.ClearCache();
        var second = TreeVariants.Bake(tree, 3, collider: true);
        Assert.Equal(first.Scenes, second.Scenes.Take(2));
        Assert.Equal(uids, second.Scenes.Take(2).Select(p => AssetDatabase.Current.GetUid(p)));
        Assert.Equal(3, System.IO.Directory.GetFiles(Full(second.Folder), "*.mfscene").Length);

        ResourceLoader.ClearCache();
        var kept = ResourceLoader.Load<ScatterObjectType>(second.TypePath);
        Assert.Equal(7f, kept.SpacingMeters);
        Assert.Equal(20f, kept.SlopeMaxDegrees);
        Assert.Equal(3, kept.Scenes.Length);
    }

    [Fact]
    public void WithoutTheColliderEachSceneIsTheBareBakedTree()
    {
        var tree = AddTree(seed: 7);
        tree.Style = TreeStyle.LowPoly;
        _editor.Tick(2);
        W.Inspector.RunHeaderAction(Tree3DInspector.ColliderAction);
        Assert.False(Inspector.CreateCollider);
        Assert.Contains("Create Collider — off", Inspector.GetHeaderRml(tree)!, StringComparison.Ordinal);
        try
        {
            var result = TreeVariants.Bake(tree, 1, Inspector.CreateCollider);
            Assert.Equal("Assets/Trees/oak_medium_lowpoly/oak_medium_lowpoly_v0.mfscene", Assert.Single(result.Scenes));
            ResourceLoader.ClearCache();
            var root = ResourceLoader.Load<PackedScene>(result.Scenes[0]).Instantiate();
            var baked = Assert.IsType<Tree3D>(root);
            Assert.False(baked.Collision);
            Assert.Equal(TreeStyle.LowPoly, baked.BakedMesh!.Style);
            Assert.Equal(7, baked.BakedMesh.Seed);
            root.Free();
        }
        finally
        {
            W.Inspector.RunHeaderAction(Tree3DInspector.ColliderAction); // the inspector is shared: back to the default
        }
    }

    [Fact]
    public void TheTypeScattersCollidingTreesOnATerrain()
    {
        var tree = AddTree();
        var result = TreeVariants.Bake(tree, 2, collider: true);
        ResourceLoader.ClearCache();
        var type = ResourceLoader.Load<ScatterObjectType>(result.TypePath);

        var data = TerrainData.Create(TerrainProfile.Realistic, 32, 1f, 16);
        data.CollisionMode = TerrainCollisionMode.None;
        data.ObjectTypes = [type];
        var terrain = new Terrain3D { Name = "Terrain", Data = data };
        _editor.Scene.AddNode(terrain, _editor.Scene.Root);
        var ids = new[] { data.AddObject(0, 8f, 8f), data.AddObject(0, 20f, 12f), data.AddObject(0, 12f, 24f) };
        _editor.Tick(2);

        Assert.Equal(3, terrain.ObjectCount);
        foreach (var id in ids)
        {
            var node = Assert.IsType<StaticBody3D>(terrain.GetObjectNode(id));
            Assert.Single(node.Children.OfType<Tree3D>());
            Assert.Single(node.Children.OfType<CollisionShape3D>());
            Assert.True(node.Position.Y < 0f); // sunk by the trunk radius into flat ground at 0
        }
    }
}
