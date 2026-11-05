using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>G6.4: mesh levels of detail — data, validation, buffer layout, selection by projected error, sort keys.</summary>
public sealed class MeshLodTests
{
    // A quad (4 vertices, 2 triangles) and a 1-triangle level over its vertices.
    private static MeshSurface Quad() => new(
        [new(-1, 0, -1), new(1, 0, -1), new(1, 0, 1), new(-1, 0, 1)],
        [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
        [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY],
        [0, 2, 1, 0, 3, 2]);

    private static MeshSurface Triangle() => new(
        [new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)], [], [], [0, 2, 1]);

    [Fact]
    public void LayoutAppendsEveryLevelAfterTheSurfaces()
    {
        var first = Quad();
        first.Lods = [new MeshLod(0.1f, [0, 2, 1]), new MeshLod(0.5f, Triangle())];
        var second = Quad();
        var mesh = new ArrayMesh();
        mesh.AddSurface(first);
        mesh.AddSurface(second);

        var levels = MeshGpu.Layout(mesh, out var ranges, out var lods, out var vertexCount, out var indexCount);

        Assert.Equal(3, levels);
        Assert.Equal(new SurfaceRange(0, 6, 0), ranges[0]);
        Assert.Equal(new SurfaceRange(6, 6, 4), ranges[1]);
        // Level 1 indexes the first surface's vertices; level 2 has its own three after both surfaces.
        Assert.Equal(new LodRange(new SurfaceRange(12, 3, 0), 0.1f), lods[0][0]);
        Assert.Equal(new LodRange(new SurfaceRange(15, 3, 8), 0.5f), lods[0][1]);
        Assert.Empty(lods[1]);
        Assert.Equal(11, vertexCount);
        Assert.Equal(18, indexCount);
    }

    [Fact]
    public void ALevelTakesItsSurfacesStreamFlag()
    {
        var surface = Quad();
        surface.Colors = [Vector4.One, Vector4.One, Vector4.One, Vector4.One];
        surface.Lods = [new MeshLod(1f, Triangle())];
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);

        MeshGpu.Layout(mesh, out var ranges, out var lods, out _, out _);

        Assert.True(ranges[0].HasStreams);
        Assert.True(lods[0][0].Range.HasStreams);
    }

    [Fact]
    public void ValidationRejectsBadLevels()
    {
        var descending = Quad();
        descending.Lods = [new MeshLod(0.5f, [0, 2, 1]), new MeshLod(0.1f, [0, 3, 2])];
        Assert.Contains("ascending", Assert.Throws<InvalidDataException>(descending.Validate).Message);

        var outOfRange = Quad();
        outOfRange.Lods = [new MeshLod(0.5f, [0, 2, 9])];
        Assert.Contains("out of range", Assert.Throws<InvalidDataException>(outOfRange.Validate).Message);

        var partial = Quad();
        partial.Lods = [new MeshLod(0.5f, [0, 2])];
        Assert.Contains("multiple of 3", Assert.Throws<InvalidDataException>(partial.Validate).Message);

        var tooMany = Quad();
        tooMany.Lods = [.. Enumerable.Range(1, MeshLod.MaxLevels).Select(i => new MeshLod(i, [0, 2, 1]))];
        Assert.Contains("LOD levels", Assert.Throws<InvalidDataException>(tooMany.Validate).Message);

        var nested = Triangle();
        nested.Lods = [new MeshLod(1f, [0, 2, 1])];
        var parent = Quad();
        parent.Lods = [new MeshLod(1f, nested)];
        Assert.Contains("LODs of its own", Assert.Throws<InvalidDataException>(parent.Validate).Message);
    }

    [Fact]
    public void ChangingTheLevelsBumpsTheMeshVersion()
    {
        var surface = Quad();
        var mesh = new ArrayMesh();
        mesh.AddSurface(surface);
        var before = mesh.Version;

        surface.Lods = [new MeshLod(0.2f, [0, 2, 1])];

        Assert.NotEqual(before, mesh.Version);
    }

    [Fact]
    public void PixelScaleMatchesThePerspectiveFormula()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 100f);

        var k = LodSelection.PixelScale(projection, 1080f);

        Assert.Equal(1080f / (2f * MathF.Tan(MathF.PI / 6f)), k, 2);
        Assert.True(LodSelection.IsPerspective(projection));
        Assert.False(LodSelection.IsPerspective(Matrix4x4.CreateOrthographic(20f, 10f, 0.1f, 100f)));
    }

    [Fact]
    public void FartherInstancesPickCoarserLevels()
    {
        LodRange[] levels = [Level(0.01f), Level(0.02f), Level(0.04f)];
        var k = 1080f / (2f * MathF.Tan(MathF.PI / 6f)); // ≈ 935 px per unit at distance 1

        int At(float distance, float bias = 1f, float threshold = 1f, float scale = 1f) =>
            LodSelection.Select(levels, LodSelection.AllowedError(k, true, distance, scale, threshold, bias));

        // 0.01 units project to 1 px at ≈ 9.35 m, 0.02 at 18.7, 0.04 at 37.4.
        Assert.Equal(0, At(5f));
        Assert.Equal(1, At(10f));
        Assert.Equal(2, At(20f));
        Assert.Equal(3, At(40f));
        Assert.Equal(3, At(1000f));
        // A higher bias keeps detail further away; a higher threshold drops it sooner; a scaled-up instance keeps detail.
        Assert.Equal(1, At(20f, bias: 2f));
        Assert.Equal(2, At(10f, threshold: 2f));
        Assert.Equal(1, At(20f, scale: 2f));
        // A threshold of 0 always draws the surface; so does a camera inside the bounds.
        Assert.Equal(0, At(1000f, threshold: 0f));
        Assert.Equal(0, At(0f));
    }

    [Fact]
    public void OrthographicSelectionIgnoresDistance()
    {
        LodRange[] levels = [Level(0.01f), Level(0.1f)];
        var projection = Matrix4x4.CreateOrthographic(20f, 10f, 0.1f, 100f); // 10 units over 1000 px: 100 px per unit
        var k = LodSelection.PixelScale(projection, 1000f);

        Assert.Equal(100f, k, 3);
        Assert.Equal(1, LodSelection.Select(levels, LodSelection.AllowedError(k, false, 1f, 1f, 1f, 1f)));
        Assert.Equal(1, LodSelection.Select(levels, LodSelection.AllowedError(k, false, 500f, 1f, 1f, 1f)));
        Assert.Equal(2, LodSelection.Select(levels, LodSelection.AllowedError(k, false, 1f, 1f, 10f, 1f)));
    }

    [Fact]
    public void MaxScaleAndDistanceHelpers()
    {
        var model = Matrix4x4.CreateScale(1f, 3f, 2f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(5, 0, 0);
        Assert.Equal(3f, LodSelection.MaxScale(model), 4);

        var box = new Aabb(new Vector3(-1), new Vector3(1));
        Assert.Equal(0f, LodSelection.Distance(Vector3.Zero, box));
        Assert.Equal(4f, LodSelection.Distance(new Vector3(5, 0, 0), box), 5);
        Assert.Equal(MathF.Sqrt(2f), LodSelection.Distance(new Vector3(2, 2, 0), box), 5);
    }

    [Fact]
    public void SortKeysKeepLevelsOfASurfaceAdjacent()
    {
        // The level is the least significant field: levels of one surface sort together, before the next surface.
        Assert.True(DrawSortKey.Opaque(1, 1, 1, 0, 0) < DrawSortKey.Opaque(1, 1, 1, 0, 1));
        Assert.True(DrawSortKey.Opaque(1, 1, 1, 0, 7) < DrawSortKey.Opaque(1, 1, 1, 1, 0));
        Assert.True(DrawSortKey.Opaque(1, 1, (1 << 17) - 1, 4095, 7) < DrawSortKey.Opaque(1, 2, 0, 0, 0));
        Assert.NotEqual(DrawSortKey.Opaque(3, 2, 1, 5, 2), DrawSortKey.Opaque(3, 2, 1, 5, 3));

        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 1, 0, 0) < MeshRenderer.CasterKey(CullMode.Back, false, 0, 1, 0, 1));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 1, 0, 7) < MeshRenderer.CasterKey(CullMode.Back, false, 0, 1, 1, 0));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, false, 0, 0xFFFFF, 4095, 7) < MeshRenderer.CasterKey(CullMode.Back, false, 1, 0, 0, 0));
        Assert.True(MeshRenderer.CasterKey(CullMode.Back, true, 0xFFFFF, 0xFFFFF, 4095, 7) < MeshRenderer.CasterKey(CullMode.Front, false, 0, 0, 0, 0));
    }

    private static LodRange Level(float error) => new(default, error);
}

/// <summary>G6.4: <c>rendering.meshLodThreshold</c> in <c>project.mfproj</c> and on the render server.</summary>
public sealed class MeshLodSettingsTests
{
    private static ProjectSettings Parse(string json) =>
        ProjectSettings.Parse(System.Text.Encoding.UTF8.GetBytes(json), "test.mfproj");

    [Fact]
    public void ThresholdRoundTripsAndIsOmittedAtItsDefault()
    {
        Assert.Equal(1f, new ProjectSettings().Rendering.MeshLodThreshold);
        Assert.DoesNotContain("meshLodThreshold", System.Text.Encoding.UTF8.GetString(new ProjectSettings().ToJson()), StringComparison.Ordinal);

        var settings = new ProjectSettings();
        settings.Rendering.MeshLodThreshold = 2.5f;
        var json = System.Text.Encoding.UTF8.GetString(settings.ToJson());
        Assert.Contains("\"meshLodThreshold\": 2.5", json, StringComparison.Ordinal);
        Assert.Equal(2.5f, Parse(json).Rendering.MeshLodThreshold);

        // Older files need no migration; a negative value is an error naming the key.
        Assert.Equal(1f, Parse("""{ "format": 2, "rendering": { "antiAliasing": "Taa" } }""").Rendering.MeshLodThreshold);
        Assert.Contains("rendering.meshLodThreshold",
            Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 2, "rendering": { "meshLodThreshold": -1 } }""")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderServerValidatesTheThreshold()
    {
        using var server = new RenderServer(new MainframeEngine.Tests.Nodes.SpineNodeTests.HeadlessRenderer());
        server.MeshLodThreshold = 0f;
        Assert.Equal(0f, server.MeshLodThreshold);
        Assert.Throws<ArgumentOutOfRangeException>(() => server.MeshLodThreshold = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => server.MeshLodThreshold = -0.5f);
    }
}
