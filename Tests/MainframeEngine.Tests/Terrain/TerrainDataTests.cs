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
        Assert.Equal("Content/Scenes/world_terrain", TerrainData.DefaultFolderFor("Content/Scenes/world.mscene"));
        Assert.Equal("forest_terrain", TerrainData.DefaultFolderFor("forest.mscene"));
    }
}
