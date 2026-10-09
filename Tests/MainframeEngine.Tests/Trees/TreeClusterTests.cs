using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>ADR 0172: leaf-cluster cards — <see cref="TreeGenerator.TwigParams"/>, <see cref="TreeClusterBaker"/> and the cluster mesher.</summary>
[Collection(nameof(Scene.SerialResources))] // presets and leaf textures go through the process-wide loader
public sealed class TreeClusterTests(ITestOutputHelper output)
{
    private static string Artifacts => Path.Combine(AppContext.BaseDirectory, "artifacts", "tree-clusters");

    private static TreeOptions Cluster(string preset)
    {
        var options = TreePresets.Load(preset);
        options.LeafMode = TreeLeafMode.Cluster;
        return options;
    }

    [Fact]
    public void TwigIsTheTreesLastLevelCarryingItsLeaves()
    {
        var tree = TreePresets.Load("Oak Medium").ToParams();
        var twig = TreeGenerator.TwigParams(tree, leafSlots: 8, leafDensity: 1.5, seed: 1, variant: 0);

        Assert.Equal(0, twig.Levels);
        Assert.Equal(12, twig.LeafCount);
        Assert.Equal(tree.LeafSize, twig.LeafSize);
        Assert.Equal(tree.LeafAngle, twig.LeafAngle);
        Assert.Equal(tree.LeafBillboard, twig.LeafBillboard);
        Assert.Equal(1.0, twig.Scale);
        var spacing = tree.Length[3] * (1 - tree.LeafStart) / tree.LeafCount;
        Assert.Equal(spacing * 8, twig.Length[0], 9);
        Assert.NotEqual(twig.Seed, TreeGenerator.TwigParams(tree, 8, 1.5, 1, variant: 1).Seed);
    }

    [Fact]
    public void BakeIsDeterministic()
    {
        var tree = TreePresets.Load("Oak Medium").ToParams();
        var twig = new TwigOptions { AtlasWidth = 256, AtlasHeight = 256 };
        var (leaf, w, h) = LeafImage();
        var first = TreeClusterBaker.Bake(tree, twig, leaf, w, h, 0.5f);
        var second = TreeClusterBaker.Bake(tree, twig, leaf, w, h, 0.5f);

        Assert.Equal(first.Card, second.Card);
        Assert.Equal(first.AlbedoPixels, second.AlbedoPixels);
        Assert.Equal(first.NormalPixels, second.NormalPixels);
        Assert.Equal(first.ThicknessPixels, second.ThicknessPixels);
    }

    [Fact]
    public void AtlasCellsHoldTwigsWithNormalsThicknessAndDilatedEdges()
    {
        var options = Cluster("Oak Medium");
        var atlas = TreeClusterBaker.GetOrBake(options);
        atlas.SavePngs(Artifacts, "oak");
        Assert.Same(atlas, TreeClusterBaker.GetOrBake(Cluster("Oak Medium"))); // cached per look

        var card = atlas.Card;
        Assert.True(card.IsValid);
        Assert.Equal((8, 8), (card.Variants, card.LeafSlots));
        Assert.Equal((3, 3), (card.Columns, card.Rows)); // round oak clusters get square cells (AutoLayout)
        Assert.InRange(card.BaseV, 0.3, 1.0); // the stem starts in the cell's lower part
        int cellW = atlas.Width / card.Columns, cellH = atlas.Height / card.Rows;
        for (var cell = 0; cell < card.Variants; cell++)
        {
            int x0 = cell % card.Columns * cellW, y0 = cell / card.Columns * cellH;
            int covered = 0, layered = 0, facing = 0, black = 0;
            for (var y = y0; y < y0 + cellH; y++)
            {
                for (var x = x0; x < x0 + cellW; x++)
                {
                    var i = (y * atlas.Width + x) * 4;
                    if (atlas.AlbedoPixels[i + 3] < 128)
                    {
                        if (atlas.AlbedoPixels[i] + atlas.AlbedoPixels[i + 1] + atlas.AlbedoPixels[i + 2] == 0)
                            black++;
                        continue;
                    }

                    covered++;
                    if (atlas.ThicknessPixels[i] > 64)
                        layered++;
                    if (atlas.NormalPixels[i + 2] > 128)
                        facing++;
                }
            }

            var total = cellW * cellH;
            output.WriteLine($"cell {cell}: coverage {100.0 * covered / total:0.0} %, thick {100.0 * layered / Math.Max(1, covered):0.0} %, facing {100.0 * facing / Math.Max(1, covered):0.0} %");
            Assert.InRange((double)covered / total, 0.08, 0.95);
            Assert.True(layered > 0 && layered < covered, $"cell {cell}: thickness is flat");
            Assert.True(facing > covered / 2, $"cell {cell}: card-space normals mostly face the card's front");
            Assert.True(black < total / 10, $"cell {cell}: {black} undilated black texels");
        }
    }

    [Fact]
    public void ClusterModeMeshesFewerLargerCardsInsideAtlasCells()
    {
        var single = TreeMesh.Generate(TreePresets.Load("Oak Medium"), 35729, TreeStyle.Realistic);
        var options = Cluster("Oak Medium");
        var cluster = TreeMesh.Generate(options, 35729, TreeStyle.Realistic);
        var card = TreeClusterBaker.GetOrBake(options).Card;

        var singleLeaves = single.Lods[0].GetSurface(1);
        var clusterLeaves = cluster.Lods[0].GetSurface(1);
        output.WriteLine($"Oak Medium leaf cards: single {singleLeaves.VertexCount / 4}, clusters {clusterLeaves.VertexCount / 4}");
        Assert.True(clusterLeaves.VertexCount * 4 <= singleLeaves.VertexCount, "cluster cards are not a quarter of the leaf cards");
        Assert.Equal(single.Lods[0].GetSurface(0).VertexCount, cluster.Lods[0].GetSurface(0).VertexCount); // the bark is unchanged

        // Every card samples one cell; flutter grows from the twig's base (0.75) to its top (< 1); normals are unit.
        for (var v = 0; v < clusterLeaves.VertexCount; v += 4)
        {
            var uvs = clusterLeaves.UVs.AsSpan(v, 4);
            var column = (int)MathF.Floor(MathF.Min(uvs[0].X, uvs[2].X) * card.Columns + 1e-4f);
            var row = (int)MathF.Floor(MathF.Min(uvs[0].Y, uvs[2].Y) * card.Rows + 1e-4f);
            foreach (var uv in uvs)
            {
                Assert.InRange(uv.X * card.Columns - column, -1e-4f, 1 + 1e-4f);
                Assert.InRange(uv.Y * card.Rows - row, -1e-4f, 1 + 1e-4f);
            }

            Assert.True(row * card.Columns + column < card.Variants);
        }

        Assert.All(clusterLeaves.Custom0, c => Assert.InRange(c.Y, 0.75f, 0.9995f));
        Assert.All(clusterLeaves.Normals, n => Assert.InRange(n.Length(), 0.999f, 1.001f));
        clusterLeaves.Validate();

        // Fewer cards per level of detail, larger at LOD1.
        Assert.True(cluster.Lods[1].GetSurface(1).VertexCount < clusterLeaves.VertexCount);
    }

    [Fact]
    public void ReportsBakeTime()
    {
        // Prints the cost of the bakes (run in Release for real numbers): a cluster atlas and an impostor of Oak Medium.
        var tree = TreePresets.Load("Oak Medium").ToParams();
        var (leaf, w, h) = LeafImage();
        TreeClusterBaker.Bake(tree, new TwigOptions { AtlasWidth = 128, AtlasHeight = 128 }, leaf, w, h, 0.5f); // JIT
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var leaves = TreeMaterials.LeafTexture(TreePresets.Load("Oak Medium"))!;
        var decoded = watch.Elapsed.TotalMilliseconds;
        var (rgba, lw, lh) = leaves.DecodePixels();
        var decodedAgain = watch.Elapsed.TotalMilliseconds;
        var atlas = TreeClusterBaker.Bake(tree, new TwigOptions(), rgba, lw, lh, 0.5f);
        var clusters = watch.Elapsed.TotalMilliseconds;
        var options = Cluster("Oak Medium");
        var mesh = TreeMesh.Generate(options, 35729, TreeStyle.Realistic);
        var generated = watch.Elapsed.TotalMilliseconds;
        mesh.GetImpostor(TreeMaterials.Bark(options, TreeStyle.Realistic), TreeMaterials.Leaves(options, TreeStyle.Realistic));
        var impostor = watch.Elapsed.TotalMilliseconds;
        output.WriteLine($"leaf texture {decoded:0} ms, decode {decodedAgain - decoded:0} ms, cluster atlas {clusters - decodedAgain:0} ms " +
                         $"({atlas.Card.Columns} x {atlas.Card.Rows}), generate {generated - clusters:0} ms, impostor {impostor - generated:0} ms");
    }

    [Fact]
    public void ClusterLeavesGetTheAtlasMaterial()
    {
        var options = Cluster("Oak Medium");
        var material = Assert.IsType<FoliageMaterial3D>(TreeMaterials.Leaves(options, TreeStyle.Realistic));
        var atlas = TreeClusterBaker.GetOrBake(options);

        Assert.Same(atlas.Albedo, material.AlbedoTexture);
        Assert.Same(atlas.Normal, material.NormalTexture);
        Assert.Same(atlas.Thickness, material.ThicknessTexture);
        Assert.True(atlas.Albedo.ImportSettings.PreserveAlphaCoverage);
        Assert.Equal(FoliageBackFace.Keep, material.BackFace);
        Assert.Equal(AlphaAntialiasing.AlphaToCoverage, material.AlphaAntialiasingMode);
        Assert.NotSame(material, TreeMaterials.Leaves(TreePresets.Load("Oak Medium"), TreeStyle.Realistic));
    }

    // A small leaf image: an opaque ellipse on transparent texels.
    private static (byte[] Rgba, int Width, int Height) LeafImage()
    {
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f - size / 2f) / (size * 0.3f);
                var dy = (y + 0.5f - size / 2f) / (size * 0.45f);
                var i = (y * size + x) * 4;
                rgba[i] = 70;
                rgba[i + 1] = 130;
                rgba[i + 2] = 50;
                rgba[i + 3] = dx * dx + dy * dy <= 1f ? (byte)255 : (byte)0;
            }
        }

        return (rgba, size, size);
    }
}
