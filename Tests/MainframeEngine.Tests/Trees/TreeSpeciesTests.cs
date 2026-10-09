using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>ADR 0172: the engine's species — <see cref="TreeLevel.LengthProfile"/>, the painted textures and the presets.</summary>
[Collection(nameof(Scene.SerialResources))] // presets go through the process-wide loader
public sealed class TreeSpeciesTests
{
    [Fact]
    public void ProfileSamplesLinearlyBetweenEvenlySpacedValues()
    {
        double[] profile = [1.0, 0.5, 0.0];
        Assert.Equal(1.0, TreeGenerator.SampleProfile(profile, 0));
        Assert.Equal(0.75, TreeGenerator.SampleProfile(profile, 0.25), 12);
        Assert.Equal(0.5, TreeGenerator.SampleProfile(profile, 0.5), 12);
        Assert.Equal(0.0, TreeGenerator.SampleProfile(profile, 1));
        Assert.Equal(0.0, TreeGenerator.SampleProfile(profile, 2)); // clamped
        Assert.Equal(0.7, TreeGenerator.SampleProfile([0.7], 0.3));
    }

    [Fact]
    public void LengthProfileShortensBranchesWhereItIsLowAndKeepsTheRngInStep()
    {
        var plain = TreePresets.Load("Oak Medium").ToParams();
        var shaped = TreePresets.Load("Oak Medium").ToParams();
        shaped.LengthProfile[1] = [1.0, 0.2]; // level 1: full length at the trunk's base, a fifth at its top
        var generator = new TreeGenerator();
        var a = generator.GrowSkeleton(plain, plain.Seed);
        var plainBranches = a.Branches.ToArray();
        var plainLeaves = a.LeafCount;
        var b = generator.GrowSkeleton(shaped, shaped.Seed);

        Assert.Equal(plainBranches.Length, b.BranchCount); // same structure, same RNG draws
        Assert.Equal(plainLeaves, b.LeafCount);
        for (var i = 0; i < plainBranches.Length; i++)
        {
            var before = plainBranches[i];
            var after = b.Branches[i];
            if (before.Level == 1 && !before.Terminal)
                Assert.Equal(before.Length * TreeGenerator.SampleProfile(shaped.LengthProfile[1], before.ParentStart), after.Length, 9);
            else if (before.Level == 0)
                Assert.Equal(before.Length, after.Length);
        }
    }

    [Theory]
    [InlineData(TreeLeafKind.Birch)]
    [InlineData(TreeLeafKind.Beech)]
    [InlineData(TreeLeafKind.Spruce)]
    [InlineData(TreeLeafKind.Fir)]
    public void LeafPaintingIsDeterministicAndCoversPartOfTheImage(TreeLeafKind kind)
    {
        var first = TreeTexturePainter.PaintLeaf(kind, 128);
        Assert.Equal(first, TreeTexturePainter.PaintLeaf(kind, 128));
        var covered = 0;
        for (var i = 3; i < first.Length; i += 4)
            if (first[i] >= 128)
                covered++;
        Assert.InRange(covered / (128.0 * 128.0), 0.05, 0.7);
    }

    [Theory]
    [InlineData(TreeBarkKind.Birch)]
    [InlineData(TreeBarkKind.Beech)]
    public void BarkTilesSeamlessly(TreeBarkKind kind)
    {
        const int size = 128;
        var (color, normal, roughness) = TreeTexturePainter.PaintBark(kind, size);
        Assert.Equal(size * size * 4, normal.Length);
        Assert.Equal(size * size * 4, roughness.Length);
        // The step across the wrap (last column → first, last row → first) is like the mean step between neighbours inside.
        double Step(int a, int b) => Math.Abs(color[a] - color[b]) + Math.Abs(color[a + 1] - color[b + 1]) + Math.Abs(color[a + 2] - color[b + 2]);
        double wrap = 0, inside = 0;
        for (var y = 0; y < size; y++)
        {
            wrap += Step((y * size + size - 1) * 4, y * size * 4) + Step(((size - 1) * size + y) * 4, y * 4);
            for (var x = 0; x < size - 1; x++)
                inside += Step((y * size + x) * 4, (y * size + x + 1) * 4) + Step((x * size + y) * 4, ((x + 1) * size + y) * 4);
        }

        inside /= size - 1;
        Assert.True(wrap <= inside * 2.5 + 1, $"the wrap steps {wrap:0} against {inside:0} for an average pair of lines inside");
    }

    [Theory]
    [InlineData("Birch Medium")]
    [InlineData("Beech Large")]
    [InlineData("Spruce Small")]
    [InlineData("Fir Medium")]
    public void SpeciesPresetsGrowTreesWithTheirPaintedTextures(string name)
    {
        var options = TreePresets.Load(name);
        Assert.Equal(name, options.ResourceName);
        var lods = new TreeGenerator().Generate(options.ToParams());
        Assert.True(lods[0].Bark.VertexCount > 100 && lods[0].Leaves.VertexCount > 100);
        Assert.True(lods[2].TriangleCount < lods[0].TriangleCount);
        var bark = Assert.IsType<FoliageMaterial3D>(TreeMaterials.Bark(options, TreeStyle.Realistic));
        Assert.NotNull(bark.AlbedoTexture);
        Assert.NotNull(bark.OrmTexture);
        Assert.NotNull(Assert.IsType<FoliageMaterial3D>(TreeMaterials.Leaves(options, TreeStyle.Realistic)).AlbedoTexture);
        if (options.Type == TreeType.Evergreen)
            Assert.Contains(options.Level[1].LengthProfile, v => v < 1.0); // a conical crown
    }
}
