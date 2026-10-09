using System.Diagnostics;
using System.Numerics;
using MainframeEngine.Tests.Physics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary><see cref="TreeScatter"/>: chunk bucketing, levels, collision and saving (ADR 0158).</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class TreeScatterTests(ITestOutputHelper output) : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    // Two species: aspens with two seeds, low-poly bushes with one (fast to generate).
    private static TreeSpecies[] Species() =>
    [
        new TreeSpecies { Preset = "Aspen Small", Seeds = [1, 2] },
        new TreeSpecies { Preset = "Bush 2", Style = TreeStyle.LowPoly },
    ];

    private static TreePlacement[] Grid(int perSide, float spacing, Vector3 origin)
    {
        var placements = new List<TreePlacement>();
        for (var z = 0; z < perSide; z++)
            for (var x = 0; x < perSide; x++)
                placements.Add(new TreePlacement(origin + new Vector3(x * spacing, 0, z * spacing), x * 0.7f, 0.8f + 0.1f * (z % 3), (x + z) % 2));
        return [.. placements];
    }

    private TreeScatter AddScatter(TreePlacement[] placements, float chunkSize = 16f)
    {
        var scatter = new TreeScatter { Name = "Forest", Species = Species(), ChunkSize = chunkSize };
        scatter.SetPlacements(placements);
        _h.Add(scatter);
        scatter.Owner = _h.Scene;
        return scatter;
    }

    [Fact]
    public void ChunksFloorNegativeCoordinates()
    {
        Assert.Equal(new Vector2I(0, 0), TreeScatter.ChunkOf(new Vector3(0.1f, 5, 15.9f), 16f));
        Assert.Equal(new Vector2I(-1, -1), TreeScatter.ChunkOf(new Vector3(-0.1f, 0, -16f), 16f));
        Assert.Equal(new Vector2I(-2, 1), TreeScatter.ChunkOf(new Vector3(-16.01f, 0, 16f), 16f));
    }

    [Fact]
    public void VariantsAreAStableHashOfThePosition()
    {
        var counts = new int[3];
        for (var i = 0; i < 300; i++)
        {
            var p = new Vector3(i * 1.37f - 200f, 0, i * 0.91f);
            var v = TreeScatter.VariantOf(p, 3);
            Assert.Equal(v, TreeScatter.VariantOf(p, 3));
            counts[v]++;
        }

        Assert.All(counts, c => Assert.InRange(c, 60, 140));
        Assert.Equal(0, TreeScatter.VariantOf(Vector3.One, 1));
    }

    [Fact]
    public void PlacementsAreBucketedPerChunkVariantAndLevel()
    {
        // 6 × 6 trees 5 m apart from (−14, −14): x and z span −14 … 11, so chunks −1 and 0 on both axes.
        var placements = Grid(6, 5f, new Vector3(-14, 0, -14));
        var scatter = AddScatter(placements);

        Assert.Equal(4, scatter.ChunkCount);
        Assert.All(scatter.Children, child => Assert.Null(child.Owner));
        var batches = scatter.Batches;
        Assert.Equal(scatter.Children.OfType<TreeScatterBatch3D>().Count(), batches.Count);

        // Expected buckets from the placements themselves.
        var expected = placements
            .GroupBy(p => (TreeScatter.ChunkOf(p.Position, 16f), p.Species,
                TreeScatter.VariantOf(p.Position, scatter.Species[p.Species].VariantCount)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        Assert.Equal(expected.Count * 3, batches.Count);

        foreach (var group in batches.GroupBy(b => (b.Chunk, b.Species, b.Variant)))
        {
            var levels = group.OrderBy(b => b.Lod).ToArray();
            Assert.Equal([0, 1, 2], levels.Select(b => b.Lod));
            var trees = expected[group.Key];
            var aabb = levels[0].CustomAabb;
            foreach (var batch in levels)
            {
                Assert.Equal(trees.Length, batch.Multimesh!.InstanceCount);
                Assert.Equal(aabb, batch.CustomAabb);
                Assert.Equal(3, batch.LodCount);
                var (begin, end) = TreeMesh.LodRange(batch.Lod, 3, scatter.Lod1Distance, scatter.Lod2Distance, scatter.MaxDistance);
                Assert.Equal(begin, batch.VisibilityRangeBegin);
                Assert.Equal(end, batch.VisibilityRangeEnd);
                Assert.Same(scatter.Species[batch.Species].GetVariant(batch.Variant)!.Lods[batch.Lod], batch.Multimesh.Mesh);
            }

            // Every tree of the bucket stands in its chunk at its position, yaw and scale.
            for (var i = 0; i < trees.Length; i++)
            {
                var transform = levels[0].Multimesh!.GetInstanceTransform(i);
                Assert.Equal(trees[i].Position, transform.Origin);
                Assert.Equal(group.Key.Chunk, TreeScatter.ChunkOf(transform.Origin, 16f));
                Assert.Equal(trees[i].Scale, transform.Basis.GetScale().Y, 1e-4f);
            }
        }

        Assert.Equal(placements.Length, batches.Where(b => b.Lod == 0).Sum(b => b.Multimesh!.InstanceCount));
    }

    [Fact]
    public void ColourVariationGoesOnTheScattersOwnMaterialCopies()
    {
        var scatter = AddScatter(Grid(4, 3f, Vector3.Zero));
        var aspen = scatter.Batches.First(b => b.Species == 0);
        var shared = aspen.GetRenderMaterial(aspen.Multimesh!.Mesh!, 1);
        Assert.Equal(0f, Assert.IsType<FoliageMaterial3D>(shared).InstanceValueJitter);

        // ADR 0175: with variation on, the batches draw copies carrying it; the species' materials stay untouched.
        scatter.InstanceValueJitter = 0.15f;
        scatter.InstanceHueJitter = 0.1f;
        _h.Run(1); // rebuilds with copies
        aspen = scatter.Batches.First(b => b.Species == 0);
        var copy = Assert.IsType<FoliageMaterial3D>(aspen.GetRenderMaterial(aspen.Multimesh!.Mesh!, 1));
        Assert.NotSame(shared, copy);
        Assert.Equal((0.15f, 0.1f), (copy.InstanceValueJitter, copy.InstanceHueJitter));
        Assert.Equal(0f, ((FoliageMaterial3D)shared).InstanceValueJitter);
        Assert.False(copy.InstanceVisibility); // per-chunk levels: only the colour differs

        // A later change reaches the copies in place.
        scatter.InstanceHueJitter = 0.05f;
        Assert.Equal(0.05f, copy.InstanceHueJitter);
    }

    [Fact]
    public void SpeciesUseTheirStylesMaterialsAndSharedVariants()
    {
        var scatter = AddScatter(Grid(4, 3f, Vector3.Zero));
        var aspen = scatter.Batches.First(b => b.Species == 0);
        var bush = scatter.Batches.First(b => b.Species == 1);
        Assert.IsType<FoliageMaterial3D>(aspen.GetRenderMaterial(aspen.Multimesh!.Mesh!, 1));
        Assert.IsType<StandardMaterial3D>(bush.GetRenderMaterial(bush.Multimesh!.Mesh!, 0));
        Assert.Equal(TreeStyle.LowPoly, scatter.Species[1].GetVariant(0)!.Style);

        // Variants are generated once per species and seed.
        Assert.Same(scatter.Species[0].GetVariant(1), scatter.Species[0].GetVariant(1));
        Assert.NotSame(scatter.Species[0].GetVariant(0), scatter.Species[0].GetVariant(1));
        Assert.Equal(2, scatter.Species[0].GetVariant(1)!.Seed);
    }

    [Fact]
    public void FarLevelsCanSkipShadowsAndDistancesUpdateInPlace()
    {
        var scatter = AddScatter(Grid(4, 3f, Vector3.Zero));
        Assert.All(scatter.Batches, b => Assert.Equal(b.Lod <= 1, b.CastShadows)); // far chunks do not cast by default
        scatter.ShadowMaxLod = 2;
        Assert.All(scatter.Batches, b => Assert.True(b.CastShadows));
        scatter.ShadowMaxLod = 0;
        Assert.All(scatter.Batches, b => Assert.Equal(b.Lod == 0, b.CastShadows));
        scatter.CastShadows = false;
        Assert.All(scatter.Batches, b => Assert.False(b.CastShadows));
        scatter.CastShadows = true;

        // ADR 0167: the coarse level casts into the sun's coarse passes from any distance, the finer ones only into the
        // fine passes (up to ShadowMaxLod), the coarser ones not at all.
        scatter.ShadowMaxLod = 1;
        scatter.ShadowCoarseLod = 1;
        Assert.All(scatter.Batches, b => Assert.Equal(b.Lod <= 1, b.CastShadows));
        Assert.All(scatter.Batches.Where(b => b.Lod == 0), b => Assert.Equal(ShadowCasterLod.Fine, b.ShadowCasterLod));
        Assert.All(scatter.Batches.Where(b => b.Lod == 1), b => Assert.Equal(ShadowCasterLod.Coarse, b.ShadowCasterLod));
        scatter.ShadowCoarseLod = -1;
        Assert.All(scatter.Batches, b => Assert.Equal(ShadowCasterLod.All, b.ShadowCasterLod));
        Assert.All(scatter.Batches, b => Assert.Equal(b.Lod <= 1, b.CastShadows));

        scatter.Lod1Distance = 12f;
        scatter.MaxDistance = 400f;
        Assert.All(scatter.Batches.Where(b => b.Lod == 1), b => Assert.Equal(12f, b.VisibilityRangeBegin));
        Assert.All(scatter.Batches.Where(b => b.Lod == 2), b => Assert.Equal(400f, b.VisibilityRangeEnd));
    }

    [Fact]
    public void EveryTreeGetsATrunkCapsuleInItsChunkBody()
    {
        var placements = Grid(6, 5f, new Vector3(-14, 0, -14));
        var scatter = AddScatter(placements);
        Assert.Equal(4, scatter.CollisionBodies.Count);
        var shapes = scatter.CollisionBodies.SelectMany(b => b.Children.OfType<CollisionShape3D>()).ToArray();
        Assert.Equal(placements.Length, shapes.Length);

        // A ray at chest height along a row of trees hits the first trunk.
        _h.Run(1);
        var first = placements[0];
        Assert.True(_h.Query.RayCast(first.Position + new Vector3(-6, 1, 0), first.Position + new Vector3(2, 1, 0), out var hit));
        var radius = scatter.Species[first.Species].GetVariant(TreeScatter.VariantOf(first.Position, scatter.Species[first.Species].VariantCount))!.TrunkRadius;
        Assert.InRange(hit.Position.X, first.Position.X - radius * first.Scale - 0.05f, first.Position.X - radius * first.Scale + 0.05f);

        scatter.Collision = false;
        _h.Run(1);
        Assert.Empty(scatter.CollisionBodies);
        Assert.Empty(scatter.Children.OfType<StaticBody3D>());
    }

    [Fact]
    public void PlacementsAreSavedAndRebuiltFromTheScene()
    {
        var placements = Grid(3, 4f, new Vector3(1, 2, 3));
        AddScatter(placements);
        var json = System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(_h.Scene));
        Assert.DoesNotContain("TreeScatterBatch3D", json, StringComparison.Ordinal);
        Assert.DoesNotContain("StaticBody3D", json, StringComparison.Ordinal);

        var scene = new PackedScene();
        scene.SetContent(System.Text.Encoding.UTF8.GetBytes(json), null);
        var copy = (Node3D)scene.Instantiate();
        var loaded = (TreeScatter)copy.GetNode("Forest")!;
        Assert.Equal(placements.Length, loaded.PlacementCount);
        for (var i = 0; i < placements.Length; i++)
            Assert.Equal(placements[i], loaded.GetPlacement(i));
        Assert.Equal(2, loaded.Species.Length);
        Assert.Equal([1, 2], loaded.Species[0].Seeds);

        _h.Scene.AddChild(copy);
        Assert.Equal(placements.Length, loaded.Batches.Where(b => b.Lod == 0).Sum(b => b.Multimesh!.InstanceCount));
    }

    [Fact]
    public void ReportsCollisionCost()
    {
        // Not a gate (timing varies with the machine and the test load): the numbers for the docs. 2,000 trees over
        // 256 m, a capsule per tree in one static body per 32 m chunk, and a character walking among them for 10 s,
        // against the same walk without the trunks.
        var placements = Grid(45, 256f / 45f, new Vector3(-128, 0, -128))[..2000];
        var results = new List<string>();
        foreach (var collision in new[] { true, false, true }) // the first run warms up (JIT)
        {
            using var h = new PhysicsHarness3D();
            h.AddFloor(size: 300f);
            var scatter = new TreeScatter { Species = Species(), ChunkSize = 32f, Collision = collision };
            scatter.SetPlacements(placements);
            foreach (var species in scatter.Species)
                for (var v = 0; v < species.VariantCount; v++)
                    species.GetVariant(v); // generate first: time the build alone
            var build = Stopwatch.StartNew();
            h.Add(scatter);
            var buildMs = build.Elapsed.TotalMilliseconds;
            var walker = new WalkerCharacter3D { Walk = new Vector3(1.5f, 0, 1.1f), Position = new Vector3(-100, 1, -100) };
            walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.35f, Height = 1.8f } });
            h.Add(walker);
            h.Run(60); // warm up
            var step = Stopwatch.StartNew();
            h.Run(600);
            results.Add($"collision {(collision ? "on" : "off")}: build {buildMs:F1} ms ({scatter.CollisionBodies.Count} bodies), " +
                        $"{step.Elapsed.TotalMilliseconds / 600:F3} ms per physics frame with a walking character");
        }

        foreach (var line in results.Skip(1))
            output.WriteLine(line);
    }

    [Fact]
    public void EmptyScatterBuildsNothing()
    {
        var scatter = AddScatter([]);
        Assert.Empty(scatter.Batches);
        Assert.Equal(0, scatter.ChunkCount);
        scatter.SetPlacements([new TreePlacement(Vector3.Zero, 0, 1, 7)]); // unknown species: skipped
        _h.Run(1);
        Assert.Empty(scatter.Batches);
    }
}
