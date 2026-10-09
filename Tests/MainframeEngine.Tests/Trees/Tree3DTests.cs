using System.Numerics;
using MainframeEngine.Tests.Physics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary><see cref="Tree3D"/>, <see cref="TreeMesh"/>, <see cref="TreeMaterials"/> and the mesh wiring (ADR 0158).</summary>
[Collection(nameof(Scene.SerialResources))] // presets and bakes go through the process-wide loader
public sealed class Tree3DTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    private Tree3D AddTree(string preset = "Aspen Small", TreeStyle style = TreeStyle.Realistic, Vector3 position = default)
    {
        var tree = _h.Add(new Tree3D { Name = "Tree", Preset = preset, Style = style, Position = position });
        tree.Owner = _h.Scene;
        return tree;
    }

    [Fact]
    public void SurfacesCarryCustom0AndLowPolyColours()
    {
        var options = TreePresets.Load("Aspen Small");
        var realistic = TreeMesh.Generate(options, options.Seed, TreeStyle.Realistic).Lods[0];
        Assert.Equal(2, realistic.SurfaceCount);
        Assert.All(Enumerable.Range(0, 2), s =>
        {
            var surface = realistic.GetSurface(s);
            Assert.Equal(surface.VertexCount, surface.Custom0.Length);
            Assert.Empty(surface.Colors);
            Assert.True(surface.HasVertexStreams);
            surface.Validate();
        });
        Assert.All(realistic.GetSurface(1).Custom0, c => Assert.Equal(1f, c.Y)); // leaves flutter

        var lowPoly = TreeMesh.Generate(options, options.Seed, TreeStyle.LowPoly).Lods[0];
        Assert.Empty(lowPoly.GetSurface(0).Colors);
        Assert.Equal(lowPoly.GetSurface(1).VertexCount, lowPoly.GetSurface(1).Colors.Length);
    }

    [Fact]
    public void LodRangesAreContiguousAndCoverEveryDistanceOnce()
    {
        Assert.Equal((0f, 30f), TreeMesh.LodRange(0, 3, 30f, 75f, 0f));
        Assert.Equal((30f, 75f), TreeMesh.LodRange(1, 3, 30f, 75f, 0f));
        Assert.Equal((75f, 300f), TreeMesh.LodRange(2, 3, 30f, 75f, 300f));
        Assert.Equal((0f, 0f), TreeMesh.LodRange(0, 1, 30f, 75f, 0f));
        foreach (var distance in (float[])[0f, 10f, 29.99f, 30f, 50f, 74.9f, 75f, 200f, 299f])
        {
            var shown = Enumerable.Range(0, 3).Count(l =>
            {
                var (begin, end) = TreeMesh.LodRange(l, 3, 30f, 75f, 300f);
                return GeometryInstance3D.IsInVisibilityRange(distance, begin, end);
            });
            Assert.Equal(1, shown);
        }

        // A level with an empty range (LOD 1 at distance 0) never draws instead of reading as unbounded.
        var (b0, e0) = TreeMesh.LodRange(0, 3, 0f, 75f, 0f);
        Assert.False(GeometryInstance3D.IsInVisibilityRange(5f, b0, e0));
        Assert.True(GeometryInstance3D.IsInVisibilityRange(5f, TreeMesh.LodRange(1, 3, 0f, 75f, 0f).Begin, 75f));
    }

    [Fact]
    public void LevelsAreUnsavedChildrenThatShowOneLevelAtAnyDistance()
    {
        var tree = AddTree();
        Assert.Equal(3, tree.LodCount);
        Assert.All(tree.Children, child => Assert.Null(child.Owner));
        Assert.Equal(3, tree.Children.OfType<TreeLod3D>().Count());
        Assert.Single(tree.Children.OfType<StaticBody3D>());

        var bounds = tree.GetLodNode(0).CustomAabb!.Value;
        for (var l = 0; l < 3; l++)
        {
            var node = tree.GetLodNode(l);
            Assert.Same(tree, node.TreeNode);
            Assert.Equal(bounds, node.CustomAabb); // one distance for every level
            Assert.True(bounds.Contains(node.Mesh!.Bounds.Min) && bounds.Contains(node.Mesh.Bounds.Max));
        }

        Assert.Equal((0f, 30f), (tree.GetLodNode(0).VisibilityRangeBegin, tree.GetLodNode(0).VisibilityRangeEnd));
        Assert.Equal((30f, 75f), (tree.GetLodNode(1).VisibilityRangeBegin, tree.GetLodNode(1).VisibilityRangeEnd));
        Assert.Equal((75f, 0f), (tree.GetLodNode(2).VisibilityRangeBegin, tree.GetLodNode(2).VisibilityRangeEnd));
        foreach (var distance in (float[])[5f, 45f, 120f])
        {
            var camera = bounds.Center + new Vector3(distance, 0f, 0f);
            var shown = Enumerable.Range(0, 3).Where(l => tree.GetLodNode(l).IsInVisibilityRange(camera, bounds)).ToArray();
            Assert.Single(shown);
            Assert.Equal(distance < 30f ? 0 : distance < 75f ? 1 : 2, shown[0]);
        }

        tree.Lod1Distance = 10f;
        tree.MaxDistance = 200f;
        Assert.Equal((10f, 75f), (tree.GetLodNode(1).VisibilityRangeBegin, tree.GetLodNode(1).VisibilityRangeEnd));
        Assert.Equal(200f, tree.GetLodNode(2).VisibilityRangeEnd);

        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_h.Scene));
        Assert.Contains("Tree3D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("TreeLod3D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticBody3D", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TrunkCapsuleStandsOnTheTreeAndFollowsCollision()
    {
        var tree = AddTree(position: new Vector3(4, 0, -3));
        var trunk = tree.Trunk;
        Assert.True(trunk.Radius > 0.05f && trunk.Height > 2f * trunk.Radius);
        Assert.Equal(trunk.Radius, tree.TrunkShape!.Radius);
        Assert.Equal(trunk.Height, tree.TrunkShape.Height);
        Assert.Equal(new Vector3(0, trunk.Height / 2f, 0), tree.TrunkBody!.Position);

        // A ray at chest height towards the trunk hits the capsule.
        _h.Run(1);
        Assert.True(_h.Query.RayCast(new Vector3(4, 1f, 5), new Vector3(4, 1f, -10), out var hit));
        Assert.Same(tree.TrunkBody, hit.Collider);
        Assert.InRange(hit.Position.Z, -3f + trunk.Radius - 0.05f, -3f + trunk.Radius + 0.05f);

        tree.Collision = false;
        _h.Run(1);
        Assert.Null(tree.TrunkBody);
        Assert.Empty(tree.Children.OfType<StaticBody3D>());
    }

    [Fact]
    public void StylesPickTheirSharedMaterials()
    {
        var realistic = AddTree();
        var lod = realistic.GetLodNode(0);
        var bark = Assert.IsType<FoliageMaterial3D>(lod.GetRenderMaterial(lod.Mesh!, 0));
        Assert.Equal(ShadingMode.Pbr, bark.ShadingMode);
        Assert.False(bark.AlphaCutout);
        Assert.Equal(FoliageBackFace.Cull, bark.BackFace);
        Assert.NotNull(bark.AlbedoTexture);
        Assert.NotNull(bark.NormalTexture);
        Assert.NotNull(bark.OrmTexture);
        var leaves = Assert.IsType<FoliageMaterial3D>(lod.GetRenderMaterial(lod.Mesh!, 1));
        Assert.True(leaves.AlphaCutout);
        Assert.Equal(FoliageBackFace.Flip, leaves.BackFace);
        Assert.Equal(0.5f, leaves.Translucency);
        Assert.Equal(ShadingMode.Pbr, leaves.ShadingMode);
        Assert.Equal(bark.WindBranchBend, leaves.WindBranchBend); // leaves stay on the swaying twigs

        // Same preset, seed and style: the same mesh and materials (instanced together).
        var twin = _h.Add(new Tree3D { Name = "Twin", Preset = "Aspen Small", Position = new Vector3(10, 0, 0) });
        Assert.Same(realistic.Mesh, twin.Mesh);
        Assert.Same(bark, twin.GetLodNode(0).GetRenderMaterial(twin.GetLodNode(0).Mesh!, 0));

        var low = _h.Add(new Tree3D { Name = "Low", Preset = "Aspen Small", Style = TreeStyle.LowPoly });
        var lowLod = low.GetLodNode(0);
        Assert.IsType<StandardMaterial3D>(lowLod.GetRenderMaterial(lowLod.Mesh!, 0));
        Assert.IsType<StandardMaterial3D>(lowLod.GetRenderMaterial(lowLod.Mesh!, 1));

        // Overrides win.
        var custom = new StandardMaterial3D();
        low.LeafMaterial = custom;
        _h.Run(1);
        Assert.Same(custom, lowLod.GetRenderMaterial(lowLod.Mesh!, 1));
    }

    [Fact]
    public void ChangesRegenerateOnTheNextFrame()
    {
        var tree = AddTree();
        var first = tree.Mesh;
        tree.Seed = 4242;
        Assert.Same(first, tree.Mesh); // deferred to the next frame
        _h.Run(1);
        Assert.NotSame(first, tree.Mesh);
        Assert.Equal(4242, tree.Mesh!.Seed);

        var options = TreePresets.Load("Aspen Small");
        tree.Options = options;
        _h.Run(1);
        Assert.Same(options, tree.Mesh!.Options);
        var before = tree.Mesh;
        options.Level[0].Length *= 0.5;   // a level edit raises Changed
        _h.Run(1);
        Assert.NotSame(before, tree.Mesh);
        Assert.True(tree.Mesh!.Lods[0].Bounds.Size.Y < before.Lods[0].Bounds.Size.Y);
    }

    [Fact]
    public void BakeRoundTripsEveryLevelAndSkipsTheGenerator()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-tree-bake", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, AssetDatabase.ContentFolder));
        var previous = AssetDatabase.Current;
        AssetDatabase.Current = new AssetDatabase(project);
        ResourceLoader.ClearCache();
        try
        {
            var tree = AddTree(style: TreeStyle.LowPoly);
            tree.Seed = 77;
            var baked = tree.Bake("Content/Trees/aspen_77.mres");
            Assert.True(File.Exists(Path.Combine(project, "Content", "Trees", "aspen_77.mres")));
            Assert.Same(baked, tree.BakedMesh);
            ResourceLoader.ClearCache();

            var loaded = ResourceLoader.Load<TreeMesh>("Content/Trees/aspen_77.mres");
            Assert.NotSame(baked, loaded);
            Assert.Equal(3, loaded.Lods.Length);
            Assert.Equal(77, loaded.Seed);
            Assert.Equal(TreeStyle.LowPoly, loaded.Style);
            Assert.Equal(baked.Trunk, loaded.Trunk);
            Assert.Equal(TreeGenerator.GeneratorVersion, loaded.GeneratorVersion);
            Assert.Equal(baked.Options!.LeafTexture, loaded.Options!.LeafTexture);
            for (var l = 0; l < 3; l++)
            {
                for (var s = 0; s < 2; s++)
                {
                    var a = baked.Lods[l].GetSurface(s);
                    var b = loaded.Lods[l].GetSurface(s);
                    Assert.Equal(a.Positions, b.Positions);
                    Assert.Equal(a.Normals, b.Normals);
                    Assert.Equal(a.UVs, b.UVs);
                    Assert.Equal(a.Custom0, b.Custom0);
                    Assert.Equal(a.Colors, b.Colors);
                    Assert.Equal(a.Indices, b.Indices);
                }
            }

            // A tree with only the bake draws it as is: no preset, no options, no generator.
            var fromBake = _h.Add(new Tree3D { Name = "Baked", BakedMesh = loaded });
            Assert.Same(loaded, fromBake.Mesh);
            Assert.Equal(3, fromBake.LodCount);
            Assert.Same(loaded.Lods[2], fromBake.GetLodNode(2).Mesh);
            Assert.IsType<StandardMaterial3D>(fromBake.GetLodNode(0).GetRenderMaterial(loaded.Lods[0], 1));
            Assert.Equal(loaded.TrunkRadius, fromBake.TrunkShape!.Radius);
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            try
            {
                Directory.Delete(project, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void TreeWithoutOptionsDrawsNothing()
    {
        var tree = _h.Add(new Tree3D { Name = "Empty" });
        Assert.Null(tree.Mesh);
        Assert.Equal(0, tree.LodCount);
        Assert.Empty(tree.Children);
        Assert.Throws<InvalidOperationException>(() => tree.Bake());
    }
}
