using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>ADR 0172: the hierarchical wind's pivot streams (<see cref="TreeParams.WindPivots"/>) and the bark's root flare and collars.</summary>
[Collection(nameof(Scene.SerialResources))] // presets go through the process-wide loader
public sealed class TreeWindTests
{
    private static TreeMeshData Generate(string preset, bool pivots, TreeLeafMode mode = TreeLeafMode.Single)
    {
        var parameters = TreePresets.Load(preset).ToParams();
        parameters.WindPivots = pivots;
        parameters.LeafMode = mode;
        if (mode == TreeLeafMode.Cluster)
            parameters.ClusterCard = new TreeClusterCard(3, 3, 8, 6, 6, 0.8, 8);
        return new TreeGenerator().Generate(parameters)[0];
    }

    [Fact]
    public void StreamsAreWrittenOnlyWhenAsked()
    {
        var plain = Generate("Oak Medium", pivots: false);
        Assert.Empty(plain.Bark.Custom1);
        Assert.Empty(plain.Leaves.Custom2);

        var pivots = Generate("Oak Medium", pivots: true);
        Assert.Equal(pivots.Bark.VertexCount, pivots.Bark.Custom1.Length);
        Assert.Equal(pivots.Bark.VertexCount, pivots.Bark.Custom2.Length);
        Assert.Equal(pivots.Leaves.VertexCount, pivots.Leaves.Custom1.Length);
        // The rest of the mesh is the same tree.
        Assert.Equal(plain.Bark.Positions, pivots.Bark.Positions);
        Assert.Equal(plain.Leaves.Custom0, pivots.Leaves.Custom0);
        var surface = pivots.Bark.ToSurface();
        Assert.True(surface.HasWindStreams);
        surface.Validate();
    }

    [Theory]
    [InlineData("Oak Medium")]
    [InlineData("Pine Medium")]
    [InlineData("Ash Large")]
    public void PivotsSitOnTheBranchChainAndTwigsBendMoreThanBranches(string preset)
    {
        var parameters = TreePresets.Load(preset).ToParams();
        parameters.WindPivots = true;
        var generator = new TreeGenerator();
        var skeleton = generator.GrowSkeleton(parameters, parameters.Seed);
        var lod = generator.Mesh(skeleton, parameters, new TreeMeshDetail());

        // Every pivot is a branch base (scaled), or the rigid trunk's origin.
        var bases = new HashSet<Vector3> { Vector3.Zero };
        foreach (var branch in skeleton.Branches)
            bases.Add(TreeGenerator.Scaled(skeleton.Sections[branch.FirstSection].Origin, parameters.Scale));

        var bark = lod.Bark;
        Assert.All(bark.Custom1.Concat(bark.Custom2), c => Assert.Contains(new Vector3(c.X, c.Y, c.Z), bases));
        var rigid = bark.Custom1.Count(c => c.W >= TreeGenerator.RigidStiffness);
        Assert.True(rigid > 0, "the trunk is not rigid");
        Assert.All(bark.Custom1, c => Assert.True(c.W is >= 0.5f and <= 40f || c.W >= TreeGenerator.RigidStiffness, $"stiffness {c.W}"));
        Assert.All(bark.Custom2, c => Assert.True(c.W is >= 0.5f and <= 40f || c.W >= TreeGenerator.RigidStiffness, $"stiffness {c.W}"));

        // A vertex's level-2 pivot is rigid exactly when it has no level-2 ancestor, and then it is its level-1 pivot.
        for (var i = 0; i < bark.VertexCount; i++)
            if (bark.Custom2[i].W >= TreeGenerator.RigidStiffness)
                Assert.Equal(new Vector3(bark.Custom1[i].X, bark.Custom1[i].Y, bark.Custom1[i].Z), new Vector3(bark.Custom2[i].X, bark.Custom2[i].Y, bark.Custom2[i].Z));

        // Every pivot lies inside the tree (it is a branch base), at or below the vertex's own height plus the branch length.
        var top = bark.Positions.Max(p => p.Y);
        Assert.All(bark.Custom1, c => Assert.InRange(c.Y, -0.01f, top));

        // Leaves turn with their twigs: every leaf pivot is a bark pivot.
        var barkPivots = bark.Custom1.Concat(bark.Custom2).Select(c => c).ToHashSet();
        Assert.All(lod.Leaves.Custom1, c => Assert.Contains(c, barkPivots));

        // Twigs (level 2) are more flexible than the branches (level 1) they grow from, on average.
        var level1 = bark.Custom1.Where(c => c.W < TreeGenerator.RigidStiffness).Select(c => c.W).DefaultIfEmpty(0f).Average();
        var level2 = bark.Custom2.Where(c => c.W < TreeGenerator.RigidStiffness).Select(c => c.W).DefaultIfEmpty(0f).Average();
        if (parameters.Levels >= 2)
            Assert.True(level2 < level1, $"twigs {level2:0.00} are stiffer than branches {level1:0.00}");
    }

    [Fact]
    public void ClusterCardsCarryTheirBranchesPivots()
    {
        var lod = Generate("Oak Medium", pivots: true, TreeLeafMode.Cluster);
        var barkPivots = lod.Bark.Custom1.ToHashSet();
        Assert.Equal(lod.Leaves.VertexCount, lod.Leaves.Custom1.Length);
        Assert.All(lod.Leaves.Custom1, c => Assert.Contains(c, barkPivots));
        // Every card's four corners share one pivot.
        for (var v = 0; v < lod.Leaves.VertexCount; v += 4)
            Assert.All(lod.Leaves.Custom1.AsSpan(v, 4).ToArray(), c => Assert.Equal(lod.Leaves.Custom1[v], c));
    }

    [Fact]
    public void RootFlareWidensTheTrunkBaseInLobesAndCollarsWeldBranches()
    {
        var plainParameters = TreePresets.Load("Oak Medium").ToParams();
        var plain = new TreeGenerator().Generate(plainParameters)[0].Bark;
        var parameters = TreePresets.Load("Oak Medium").ToParams();
        parameters.RootFlare = 1.8;
        parameters.RootFlareHeight = 1.0;
        parameters.CollarScale = 1.3;
        var generator = new TreeGenerator();
        var skeleton = generator.GrowSkeleton(parameters, parameters.Seed);
        var lods = generator.Generate(parameters);
        var bark = lods[0].Bark;

        // Extra rings: three on the trunk, one per side branch (at full detail); fewer at the coarser levels.
        var segments = skeleton.Branches.Select(b => TreeGenerator.SegmentsFor(b.SegmentCount, 1) + 1).ToArray();
        var sideBranches = skeleton.Branches.Select((b, i) => (b, i)).Where(x => x.b.Parent >= 0 && !x.b.Terminal).Sum(x => segments[x.i]);
        Assert.Equal(plain.VertexCount + 3 * segments[0] + sideBranches, bark.VertexCount);
        Assert.True(lods[1].Bark.VertexCount < bark.VertexCount);

        // The ground ring is up to 1.8 × wider, in lobes (not round).
        var ground = bark.Positions.Take(segments[0]).Select(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z)).ToArray();
        var plainGround = plain.Positions.Take(segments[0]).Select(p => MathF.Sqrt(p.X * p.X + p.Z * p.Z)).Max();
        Assert.InRange(ground.Max() / plainGround, 1.5f, 2.3f);
        Assert.True(ground.Max() / ground.Min() > 1.2f, "the flare has no lobes");
        Assert.True(bark.Positions.Any(p => p.Y > 0.05f && p.Y < 1f), "no extra rings inside the flare");

        // Normals stay unit; a collared branch's first ring is wider than the plain tree's.
        Assert.All(bark.Normals, n => Assert.InRange(n.Length(), 0.999f, 1.001f));
        Assert.True(bark.Positions.Max(p => p.Y) <= plain.Positions.Max(p => p.Y) + 1e-3f);
    }

    [Fact]
    public void PivotsAreDeterministic()
    {
        var a = Generate("Ash Large", pivots: true);
        var b = Generate("Ash Large", pivots: true);
        Assert.Equal(a.Bark.Custom1, b.Bark.Custom1);
        Assert.Equal(a.Leaves.Custom2, b.Leaves.Custom2);
    }
}
