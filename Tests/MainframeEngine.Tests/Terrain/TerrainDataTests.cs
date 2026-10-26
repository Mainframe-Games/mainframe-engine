namespace MainframeEngine.Tests.Terrain;

public sealed class TerrainDataTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mf-terrain-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    internal static float Hills(float x, float z) =>
        6f * MathF.Sin(x * 0.11f) * MathF.Cos(z * 0.083f) + 1.5f * MathF.Sin(x * 0.7f + z * 0.45f) + 2f;

    private static TerrainData Small(TerrainProfile profile = TerrainProfile.Realistic) =>
        TerrainData.Create(profile, 32, profile == TerrainProfile.Realistic ? 1f : 2f, chunkMeters: profile == TerrainProfile.Realistic ? 16 : 16);

    [Fact]
    public void CreateGivesProfileDefaultsAndDerivedSizes()
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, 256, 0.5f, 32);
        Assert.Equal(512, data.Quads);
        Assert.Equal(513, data.VerticesPerSide);
        Assert.Equal(64, data.ChunkQuads);
        Assert.Equal(8, data.ChunksPerSide);
        Assert.Equal(512, data.CellsPerSide);
        Assert.Equal(-64f, data.HeightMin);
        Assert.Equal(192f, data.HeightMax);

        var faceted = TerrainData.Create(TerrainProfile.Faceted, 64, 2f);
        Assert.Equal(32, faceted.Quads);
        Assert.Equal(16, faceted.ChunkQuads);
        Assert.Equal(64, faceted.CellsPerSide);   // cells are half a quad
        Assert.Equal(1f, faceted.CellSize);
    }

    [Theory]
    [InlineData(100, 1f, 30)]   // 30 quads per chunk do not divide 100
    [InlineData(64, 1f, 3)]     // odd chunk
    [InlineData(64, 0.7f, 16)]  // not a whole number of quads
    [InlineData(4096, 1f, 64)]  // too big
    public void InvalidKnobsAreRejected(int size, float spacing, int chunk)
    {
        var data = new TerrainData { SizeMeters = size, VertexSpacing = spacing, ChunkMeters = chunk };
        Assert.Throws<InvalidDataException>(data.Validate);
    }

    [Fact]
    public void KnobsAreFixedOnceLoaded()
    {
        var data = Small();
        Assert.Throws<InvalidOperationException>(() => data.SizeMeters = 64);
        Assert.Throws<InvalidOperationException>(() => data.HeightMax = 10);
        data.CastShadows = false; // not a layout knob
    }

    [Fact]
    public void QuantisingIsIdempotentForEveryValue()
    {
        var data = Small();
        for (var v = 0; v <= 65535; v++)
        {
            var h = data.DecodeHeight((ushort)v);
            Assert.Equal(v, data.EncodeHeight(h));
            Assert.Equal(h, data.QuantizeHeight(h));
        }

        Assert.Equal(data.HeightMax, data.QuantizeHeight(1e6f));
        Assert.Equal(data.HeightMin, data.QuantizeHeight(-1e6f));
    }

    [Fact]
    public void WritesAreQuantised()
    {
        var data = Small();
        data.SetHeightsFrom(Hills);
        var h = data.Heights;
        for (var i = 0; i < h.Length; i++)
            Assert.Equal(data.QuantizeHeight(h[i]), h[i]);
        Assert.Equal(data.QuantizeHeight(Hills(3, 5)), h[5 * data.VerticesPerSide + 3]);
    }

    [Fact]
    public void LayersSaveAndLoadExactly()
    {
        var data = Small();
        data.SetHeightsFrom(Hills);
        data.SetWeightsFrom((x, z, w) =>
        {
            w[(int)(x / 4) % 8] = 2f;
            w[(int)(z / 4) % 8] += 1f;
        });
        data.SetWaterDepthFrom((x, z) => x < 6 ? 0.9f : 0f);
        data.SetCells(TerrainLayers.User, 0, new Rect2I(3, 4, 2, 1), [0x11223344u, 0xAABBCCDDu]);
        data.SaveLayers(_folder);

        var loaded = new TerrainData
        {
            SizeMeters = data.SizeMeters,
            VertexSpacing = data.VertexSpacing,
            ChunkMeters = data.ChunkMeters,
            HeightMin = data.HeightMin,
            HeightMax = data.HeightMax,
            LayerFolder = _folder,
        };
        loaded.EnsureLoaded();
        Assert.Equal(data.Heights.ToArray(), loaded.Heights.ToArray());
        Assert.Equal(data.BedHeights.ToArray(), loaded.BedHeights.ToArray());
        foreach (var (layer, image) in new[] { (TerrainLayers.Surface, 0), (TerrainLayers.Surface, 1), (TerrainLayers.User, 0) })
        {
            var all = new Rect2I(0, 0, data.CellsPerSide, data.CellsPerSide);
            var a = new uint[all.Area];
            var b = new uint[all.Area];
            data.GetCells(layer, image, all, a);
            loaded.GetCells(layer, image, all, b);
            Assert.Equal(a, b);
        }

        var vertices = new Rect2I(0, 0, data.VerticesPerSide, data.VerticesPerSide);
        var wa = new uint[vertices.Area];
        var wb = new uint[vertices.Area];
        data.GetCells(TerrainLayers.Water, 0, vertices, wa);
        loaded.GetCells(TerrainLayers.Water, 0, vertices, wb);
        Assert.Equal(wa, wb);
    }

    [Fact]
    public void DefaultLayersAreNotWrittenAndMissingFilesLoadAsDefaults()
    {
        var data = Small();
        data.SaveLayers(_folder);
        Assert.Empty(Directory.GetFiles(_folder));

        var loaded = new TerrainData { SizeMeters = 32, ChunkMeters = 16, LayerFolder = _folder };
        loaded.EnsureLoaded();
        Assert.All(loaded.Heights.ToArray(), h => Assert.Equal(loaded.QuantizeHeight(0f), h));
        Assert.Equal(1f, loaded.GetLayerWeight(0, 3, 3));
        Assert.Equal(0, loaded.GetSurface(10, 10));
        Assert.Equal(0f, loaded.GetWaterFraction(4, 4));
    }

    [Fact]
    public void OnlyDirtyLayersAreRewritten()
    {
        var data = Small();
        data.SetHeightsFrom(Hills);
        data.SetCells(TerrainLayers.User, 0, new Rect2I(0, 0, 1, 1), [7u]);
        data.SaveLayers(_folder);
        var heightmap = Path.Combine(_folder, TerrainData.HeightmapFile);
        var user = Path.Combine(_folder, TerrainData.UserFile);
        Assert.True(File.Exists(heightmap));
        File.WriteAllBytes(heightmap, [1, 2, 3]); // a rewrite would replace this

        data.SetCells(TerrainLayers.User, 0, new Rect2I(1, 0, 1, 1), [9u]);
        data.SaveLayers(_folder);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(heightmap));
        Assert.True(File.Exists(user));

        // Back to the default: the file goes.
        data.SetCells(TerrainLayers.User, 0, new Rect2I(0, 0, 2, 1), [0u, 0u]);
        data.SaveLayers(_folder);
        Assert.False(File.Exists(user));
    }

    [Fact]
    public void WrongSizedLayerFilesAreRejected()
    {
        Directory.CreateDirectory(_folder);
        Png.WriteGray16(Path.Combine(_folder, TerrainData.HeightmapFile), 5, 5, new ushort[25]);
        var data = new TerrainData { SizeMeters = 32, ChunkMeters = 16, LayerFolder = _folder };
        Assert.Throws<InvalidDataException>(data.EnsureLoaded);
    }

    [Fact]
    public void WeightsAreNormalisedToExactly255()
    {
        Span<byte> result = stackalloc byte[8];
        TerrainData.NormalizeWeights([1, 1, 1, 0, 0, 0, 0, 0], result);
        Assert.Equal(255, Sum(result));
        Assert.Equal([85, 85, 85, 0, 0, 0, 0, 0], result.ToArray());
        TerrainData.NormalizeWeights([0.3f, 0.7f, 0.11f, 5, 0, 0, 0.01f, 2], result);
        Assert.Equal(255, Sum(result));
        TerrainData.NormalizeWeights([0, 0, 0, 0, 0, 0, 0, 0], result);
        Assert.Equal([255, 0, 0, 0, 0, 0, 0, 0], result.ToArray());
        TerrainData.NormalizeWeights([float.NaN, -1, 0, 0, 0, 0, 2, 0], result);
        Assert.Equal(255, result[6]);

        static int Sum(ReadOnlySpan<byte> b)
        {
            var s = 0;
            foreach (var v in b) s += v;
            return s;
        }
    }

    [Fact]
    public void SurfaceIsTheStrongestLayerAndPaintKeepsTheSum()
    {
        var data = Small();
        var weights = new float[8];
        weights[2] = 1;
        weights[6] = 3;
        data.SetWeights(new Rect2I(4, 4, 1, 1), weights);
        Assert.Equal(6, data.GetSurface(4, 4));
        Assert.Equal(0.75f, data.GetLayerWeight(6, 4, 4), 5e-3f);

        data.PaintSurface(new System.Numerics.Vector2(4.5f, 4.5f), 0.4f, 1, 0.9f);
        Assert.Equal(1, data.GetSurface(4, 4));
        var texels = new uint[2];
        data.GetCells(TerrainLayers.Surface, 0, new Rect2I(4, 4, 1, 1), texels.AsSpan(0, 1));
        data.GetCells(TerrainLayers.Surface, 1, new Rect2I(4, 4, 1, 1), texels.AsSpan(1, 1));
        var sum = 0;
        foreach (var t in texels)
            for (var k = 0; k < 4; k++)
                sum += (int)(t >> (8 * k)) & 0xFF;
        Assert.Equal(255, sum);
        Assert.Equal(0, data.GetSurface(10, 10)); // untouched
    }

    [Fact]
    public void SplatTexturesFollowEdits()
    {
        var data = Small();
        var texture = data.GetSplatTexture(0);
        Assert.Same(texture, data.GetSplatTexture(0));
        Assert.Equal((32, 32), (texture.Width, texture.Height));
        var version = texture.Version;
        var weights = new float[8];
        weights[1] = 1;
        data.SetWeights(new Rect2I(2, 3, 1, 1), weights);
        Assert.NotEqual(version, texture.Version);
        var (pixels, width, _) = texture.DecodePixels();
        Assert.Equal(255, pixels[(3 * width + 2) * 4 + 1]);
        Assert.Equal(0, pixels[(3 * width + 2) * 4]);
    }

    // ── Layers 8–11: the third splat map (splat-2.png) ─────────────────────────

    private TerrainData LoadFrom(TerrainData like) => new()
    {
        SizeMeters = like.SizeMeters,
        VertexSpacing = like.VertexSpacing,
        ChunkMeters = like.ChunkMeters,
        HeightMin = like.HeightMin,
        HeightMax = like.HeightMax,
        LayerFolder = _folder,
    };

    private static int CellSum(TerrainData data, int u, int v)
    {
        var sum = 0;
        for (var layer = 0; layer < TerrainData.MaxLayers; layer++)
            sum += (int)MathF.Round(data.GetLayerWeight(layer, u, v) * 255f);
        return sum;
    }

    [Fact]
    public void TwelveLayersSaveToAThirdMapAndLoadExactly()
    {
        var data = Small();
        Assert.Equal(2, data.SplatImageCount);
        data.SetWeightsFrom((x, z, w) =>
        {
            Assert.Equal(TerrainData.MaxLayers, w.Length);
            w[(int)(x / 3) % 12] = 2f;
            w[(int)(z / 5) % 12] += 1f;
        });
        Assert.Equal(3, data.SplatImageCount);
        data.SaveLayers(_folder);
        Assert.True(File.Exists(Path.Combine(_folder, TerrainData.SplatFile(2))));
        Assert.Equal("splat-2.png", TerrainData.SplatFile(2));

        var loaded = LoadFrom(data);
        loaded.EnsureLoaded();
        Assert.Equal(3, loaded.SplatImageCount);
        var all = new Rect2I(0, 0, data.CellsPerSide, data.CellsPerSide);
        for (var image = 0; image < 3; image++)
        {
            var a = new uint[all.Area];
            var b = new uint[all.Area];
            data.GetCells(TerrainLayers.Surface, image, all, a);
            loaded.GetCells(TerrainLayers.Surface, image, all, b);
            Assert.Equal(a, b);
        }

        for (var u = 0; u < data.CellsPerSide; u += 3)
        {
            Assert.Equal(data.GetSurface(u, u), loaded.GetSurface(u, u));
            Assert.Equal(255, CellSum(loaded, u, u));
            for (var layer = 8; layer < 12; layer++)
                Assert.Equal(data.GetLayerWeight(layer, u, 5), loaded.GetLayerWeight(layer, u, 5));
        }
    }

    [Fact]
    public void EightLayerTerrainsWriteNoThirdMapAndTheBytesTheyAlwaysDid()
    {
        var data = Small();
        static float Weight(int u, int v, int l) => (u * 7 + v * 3 + l * 5) % 11 * 0.37f + (l == (u + v) % 8 ? 2f : 0f);

        // Eight per cell, as callers always passed them.
        var cells = new Rect2I(0, 0, data.CellsPerSide, data.CellsPerSide);
        var weights = new float[cells.Area * 8];
        for (var v = 0; v < cells.Size.Y; v++)
            for (var u = 0; u < cells.Size.X; u++)
                for (var l = 0; l < 8; l++)
                    weights[(v * cells.Size.X + u) * 8 + l] = Weight(u, v, l);
        data.SetWeights(cells, weights);
        Assert.Equal(2, data.SplatImageCount);
        data.SaveLayers(_folder);
        Assert.False(File.Exists(Path.Combine(_folder, TerrainData.SplatFile(2))));

        // What the two-map format wrote: each cell normalised over its eight weights, layers 0–3 then 4–7, as PNGs.
        var expected0 = new byte[cells.Area * 4];
        var expected1 = new byte[cells.Area * 4];
        Span<byte> normalised = stackalloc byte[8];
        for (var c = 0; c < cells.Area; c++)
        {
            TerrainData.NormalizeWeights(weights.AsSpan(c * 8, 8), normalised);
            normalised[..4].CopyTo(expected0.AsSpan(c * 4));
            normalised[4..].CopyTo(expected1.AsSpan(c * 4));
        }

        var reference = Path.Combine(_folder, "reference");
        Directory.CreateDirectory(reference);
        Png.WriteRgba8(Path.Combine(reference, "0.png"), cells.Size.X, cells.Size.Y, expected0);
        Png.WriteRgba8(Path.Combine(reference, "1.png"), cells.Size.X, cells.Size.Y, expected1);
        Assert.Equal(File.ReadAllBytes(Path.Combine(reference, "0.png")), File.ReadAllBytes(Path.Combine(_folder, TerrainData.SplatFile(0))));
        Assert.Equal(File.ReadAllBytes(Path.Combine(reference, "1.png")), File.ReadAllBytes(Path.Combine(_folder, TerrainData.SplatFile(1))));

        // A generator and painting below layer 8 keep it at two maps too.
        data.SetWeightsFrom((x, _, w) => w[(int)x % 8] = 1f);
        data.PaintSurface(new System.Numerics.Vector2(8, 8), 4f, 7, 0.5f);
        Assert.Equal(2, data.SplatImageCount);
        var none = new uint[cells.Area];
        data.GetCells(TerrainLayers.Surface, 2, cells, none);
        Assert.All(none, t => Assert.Equal(0u, t));
    }

    [Fact]
    public void AStaleThirdMapIsDeletedOnSave()
    {
        var data = Small();
        data.PaintSurface(new System.Numerics.Vector2(4, 4), 2f, 9);
        data.SaveLayers(_folder);
        var splat2 = Path.Combine(_folder, TerrainData.SplatFile(2));
        Assert.True(File.Exists(splat2));

        // Layers 8–11 painted out again: the map is all zero, so its file goes.
        data.SetWeights(new Rect2I(0, 0, data.CellsPerSide, data.CellsPerSide), new float[data.CellsPerSide * data.CellsPerSide * 8]);
        data.SaveLayers(_folder);
        Assert.False(File.Exists(splat2));

        // A terrain that never had one, saved over a folder holding one.
        var other = Small();
        other.SetHeightsFrom(Hills);
        File.WriteAllBytes(splat2, [1, 2, 3]);
        other.SaveLayers(_folder);
        Assert.False(File.Exists(splat2));
        var loaded = LoadFrom(other);
        loaded.EnsureLoaded();
        Assert.Equal(2, loaded.SplatImageCount);
        Assert.Equal(0f, loaded.GetLayerWeight(9, 4, 4));
    }

    [Fact]
    public void SetWeightsTakesOneToTwelveWeightsPerCell()
    {
        var data = Small();
        var nine = new float[2 * 9];
        nine[2] = 1f;      // cell (4, 4): layer 2 and the ninth layer
        nine[8] = 3f;
        nine[9 + 8] = 1f;  // cell (5, 4): only layer 8
        data.SetWeights(new Rect2I(4, 4, 2, 1), nine);
        Assert.Equal(3, data.SplatImageCount);
        Assert.Equal(8, data.GetSurface(4, 4));
        Assert.Equal(0.75f, data.GetLayerWeight(8, 4, 4), 5e-3f);
        Assert.Equal(0.25f, data.GetLayerWeight(2, 4, 4), 5e-3f);
        Assert.Equal(1f, data.GetLayerWeight(8, 5, 4));
        Assert.Equal(255, CellSum(data, 4, 4));

        var twelve = new float[12];
        twelve[9] = 3f;
        twelve[2] = 1f;
        data.SetWeights(new Rect2I(6, 6, 1, 1), twelve);
        Assert.Equal(9, data.GetSurface(6, 6));
        Assert.Equal(0.75f, data.GetLayerWeight(9, 6, 6), 5e-3f);
        Assert.Equal(255, CellSum(data, 6, 6));

        var one = new float[] { 1f, 1f }; // one per cell: layer 0 only
        data.SetWeights(new Rect2I(4, 4, 2, 1), one);
        Assert.Equal(1f, data.GetLayerWeight(0, 4, 4));
        Assert.Equal(0f, data.GetLayerWeight(8, 5, 4));

        Assert.Throws<ArgumentException>(() => data.SetWeights(new Rect2I(0, 0, 1, 1), new float[13]));
        Assert.Throws<ArgumentException>(() => data.SetWeights(new Rect2I(0, 0, 2, 1), new float[9]));  // not a multiple of 2
        Assert.Throws<ArgumentException>(() => data.SetWeights(new Rect2I(0, 0, 2, 1), []));
    }

    [Fact]
    public void PaintingLayerTenMakesTheThirdMapAndKeepsTheSum()
    {
        var data = Small();
        var array = data.GetSplatArray();
        Assert.Equal(2, array.Layers);
        Assert.Same(array, data.GetSplatArray());
        var texture = data.GetSplatTexture(2);
        Assert.All(texture.DecodePixels().Rgba, b => Assert.Equal(0, b));

        data.PaintSurface(new System.Numerics.Vector2(8.5f, 8.5f), 0.4f, 10, 0.75f);
        Assert.Equal(3, data.SplatImageCount);
        Assert.Equal(10, data.GetSurface(8, 8));
        Assert.Equal(0.75f, data.GetLayerWeight(10, 8, 8), 5e-3f);
        Assert.Equal(0.25f, data.GetLayerWeight(0, 8, 8), 5e-3f);
        Assert.Equal(255, CellSum(data, 8, 8));
        Assert.Equal(0, data.GetSurface(10, 10)); // untouched

        // The textures follow: map 2's texture and a new three-layer array.
        var (pixels, width, _) = texture.DecodePixels();
        Assert.Equal((int)MathF.Round(data.GetLayerWeight(10, 8, 8) * 255f), pixels[(8 * width + 8) * 4 + 2]);
        var three = data.GetSplatArray();
        Assert.NotSame(array, three);
        Assert.Equal(3, three.Layers);
        Assert.Equal(pixels, three.GetLayerPixels(2).ToArray());

        // Painting a lower layer back over it moves weight out of layer 10 and keeps the sum.
        data.PaintSurface(new System.Numerics.Vector2(8.5f, 8.5f), 0.4f, 3, 1f);
        Assert.Equal(3, data.GetSurface(8, 8));
        Assert.Equal(0f, data.GetLayerWeight(10, 8, 8));
        Assert.Equal(255, CellSum(data, 8, 8));
        Assert.Same(three, data.GetSplatArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => data.PaintSurface(new System.Numerics.Vector2(8, 8), 1f, 12));
        Assert.Throws<ArgumentOutOfRangeException>(() => data.GetSplatTexture(3));
    }

    [Fact]
    public void WaterLowersTheBed()
    {
        var data = Small();
        data.SetHeightsFrom((_, _) => 5f);
        data.SetWaterDepth(new Rect2I(2, 2, 1, 1), [0.6f]);
        var k = 2 * data.VerticesPerSide + 2;
        Assert.Equal(data.Heights[k] - MathF.Round(0.6f / 1.2f * 255) / 255f * 1.2f, data.BedHeights[k], 1e-5f);
        Assert.Equal(data.Heights[k + 1], data.BedHeights[k + 1]);
    }

    [Fact]
    public void DefaultFolderSitsBesideTheScene()
    {
        Assert.Equal("Assets/Scenes/world_terrain", TerrainData.DefaultFolderFor("Assets/Scenes/world.mscene"));
        Assert.Equal("forest_terrain", TerrainData.DefaultFolderFor("forest.mscene"));
    }
}
