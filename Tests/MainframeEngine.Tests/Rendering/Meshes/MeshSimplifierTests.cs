using System.Numerics;

namespace MainframeEngine.Tests.Rendering.Meshes;

/// <summary>ADR 0256: generated levels of detail through the mfmeshopt native library (meshoptimizer).</summary>
[Collection(nameof(Tests.Scene.SerialResources))]
public sealed class MeshSimplifierTests
{
    private static MeshSurface Sphere()
    {
        var source = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 64, Rings = 32 }.GetSurface(0);
        return new MeshSurface(source.Positions, source.Normals, source.UVs, source.Indices);
    }

    // An open, gently bumpy (N × N) grid of quads in x/z, 0..N.
    private static MeshSurface Grid(int n = 24)
    {
        var positions = new Vector3[(n + 1) * (n + 1)];
        var normals = new Vector3[positions.Length];
        for (var z = 0; z <= n; z++)
            for (var x = 0; x <= n; x++)
            {
                positions[z * (n + 1) + x] = new Vector3(x, 0.3f * MathF.Sin(x * 0.7f) * MathF.Cos(z * 0.5f), z);
                normals[z * (n + 1) + x] = Vector3.UnitY;
            }

        var indices = new List<int>();
        for (var z = 0; z < n; z++)
            for (var x = 0; x < n; x++)
            {
                var a = z * (n + 1) + x;
                indices.AddRange([a, a + n + 1, a + 1, a + 1, a + n + 1, a + n + 2]);
            }

        return new MeshSurface(positions, normals, [], [.. indices]);
    }

    [Fact]
    public void TheLibraryLoadsWithTheEnginesAbi()
    {
        Assert.True(MeshSimplifier.IsAvailable, MeshSimplifier.Problem);
        Assert.Null(MeshSimplifier.Problem);
    }

    [Fact]
    public void ASphereHalvesPerLevelWithAscendingErrors()
    {
        var surface = Sphere();
        var lods = MeshSimplifier.GenerateLods(surface);
        Assert.InRange(lods.Length, 4, MeshLod.MaxLevels - 1);
        var previousCount = surface.IndexCount;
        var previousError = 0f;
        foreach (var lod in lods)
        {
            Assert.Null(lod.Geometry); // index-only: the surface's own vertices
            Assert.Equal(0, lod.Indices.Length % 3);
            Assert.True(lod.Indices.Length <= previousCount * MeshSimplifier.MinimumReduction, $"{lod.Indices.Length} of {previousCount}");
            Assert.True(lod.Indices.Length >= 3 * MeshSimplifier.MinimumTriangles);
            Assert.All(lod.Indices, i => Assert.InRange(i, 0, surface.VertexCount - 1));
            Assert.True(lod.Error >= previousError);
            Assert.True(float.IsFinite(lod.Error));
            previousCount = lod.Indices.Length;
            previousError = lod.Error;
        }

        // The first halvings of a smooth sphere barely move it; coarse levels pay for its UV seam (meshoptimizer keeps it).
        Assert.True(lods[0].Error is > 0f and < 0.02f, $"first level error {lods[0].Error}");
        Assert.True(lods[2].Error < 0.05f, $"third level error {lods[2].Error}");
        surface.Lods = lods;
        surface.Validate();
    }

    [Fact]
    public void LockedBordersKeepTheOutline()
    {
        const int n = 24;
        var surface = Grid(n);
        var free = MeshSimplifier.GenerateLods(surface);
        var locked = MeshSimplifier.GenerateLods(surface, lockBorders: true);
        Assert.NotEmpty(free);
        Assert.NotEmpty(locked);
        var used = locked[^1].Indices.ToHashSet();
        for (var i = 0; i <= n; i++)
        {
            int[] border = [i, n * (n + 1) + i, i * (n + 1), i * (n + 1) + n];
            Assert.All(border, v => Assert.Contains(v, used));
        }

        Assert.True(free[^1].Indices.Length < locked[^1].Indices.Length);
    }

    [Fact]
    public void SmallSurfacesGetNoLevels()
    {
        var quad = new MeshSurface([new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)], [], [], [0, 2, 1, 0, 3, 2]);
        Assert.Empty(MeshSimplifier.GenerateLods(quad));
        Assert.Empty(MeshSimplifier.GenerateLods(new MeshSurface()));
    }

    [Fact]
    public void GenerateSkipsAuthoredAndSkinnedSurfacesAndSharesMeshes()
    {
        var shared = new ArrayMesh();
        shared.AddSurface(Sphere());
        var authored = new ArrayMesh();
        var withLevels = Sphere();
        withLevels.Lods = [new MeshLod(0.5f, [0, 1, 2])];
        authored.AddSurface(withLevels);
        var skinned = Sphere();
        skinned.Joints = new int[skinned.VertexCount * 4];
        skinned.Weights = Enumerable.Repeat(new Vector4(1, 0, 0, 0), skinned.VertexCount).ToArray();
        var skinnedMesh = new ArrayMesh();
        skinnedMesh.AddSurface(skinned);

        var root = new Node3D { Name = "Root" };
        root.AddChild(new MeshInstance3D { Name = "A", Mesh = shared });
        root.AddChild(new MeshInstance3D { Name = "B", Mesh = shared }); // the same mesh: generated once
        root.AddChild(new MeshInstance3D { Name = "Authored", Mesh = authored });
        root.AddChild(new MeshInstance3D { Name = "Skinned", Mesh = skinnedMesh });
        try
        {
            Assert.Equal(1, ModelLods.Generate(root, "test"));
            Assert.NotEmpty(shared.GetSurface(0).Lods);
            Assert.Single(authored.GetSurface(0).Lods);
            Assert.Empty(skinned.Lods);
        }
        finally
        {
            root.Free();
        }
    }
}
