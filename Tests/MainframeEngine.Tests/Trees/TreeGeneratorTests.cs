using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>
/// <see cref="TreeGenerator"/> beyond parity: determinism, the allocation budget, the engine's vertex data (Custom0),
/// levels of detail, the LowPoly style, the trunk capsule and conversion to engine meshes.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class TreeGeneratorTests(ITestOutputHelper output)
{
    public static TheoryData<string> Presets() => [.. TreePresets.Names];

    private static TreeParams Preset(string name) => TreePresets.Load(name).ToParams();

    // ---------------------------------------------------------------------------------------------------------------
    // Determinism and cost
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void GenerationIsDeterministicAcrossRunsGeneratorsAndThreads()
    {
        var parameters = Preset("Oak Medium");
        var generator = new TreeGenerator();
        var first = Bytes(generator.Generate(parameters));
        var again = Bytes(generator.Generate(parameters));
        var other = Bytes(new TreeGenerator().Generate(parameters));
        byte[]? threaded = null;
        var thread = new Thread(() => threaded = Bytes(new TreeGenerator().Generate(parameters)));
        thread.Start();
        thread.Join();

        Assert.Equal(first, again);
        Assert.Equal(first, other);
        Assert.Equal(first, threaded);

        var lowPoly = Bytes(generator.Generate(parameters, TreeStyle.LowPoly));
        Assert.Equal(lowPoly, Bytes(new TreeGenerator().Generate(parameters, TreeStyle.LowPoly)));
    }

    [Fact]
    public void DifferentSeedsGrowDifferentTrees()
    {
        var parameters = Preset("Oak Medium");
        var generator = new TreeGenerator();
        var a = generator.Mesh(generator.GrowSkeleton(parameters, 1), parameters, new TreeMeshDetail());
        var b = generator.Mesh(generator.GrowSkeleton(parameters, 2), parameters, new TreeMeshDetail());
        Assert.NotEqual(a.Bark.Positions, b.Bark.Positions);
    }

    [Fact]
    public void ReusedGeneratorAllocatesOnlyItsOutputArrays()
    {
        // Budget (docs/design/procedural-trees.md): on a reused generator and skeleton, Oak Medium with three Realistic
        // levels of detail allocates its output arrays plus at most 16 KB.
        var parameters = Preset("Oak Medium");
        var generator = new TreeGenerator();
        var skeleton = new TreeSkeleton();
        var lods = TreeGenerator.DefaultLods(parameters, TreeStyle.Realistic);
        Run(); // warm up: lists and scratch buffers reach their size

        var before = GC.GetAllocatedBytesForCurrentThread();
        var meshes = Run();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long arrays = 0;
        foreach (var mesh in meshes)
            arrays += ArrayBytes(mesh.Bark) + ArrayBytes(mesh.Leaves);
        output.WriteLine($"Oak Medium, 3 LODs: allocated {allocated:N0} B, output arrays {arrays:N0} B, overhead {allocated - arrays:N0} B");
        Assert.True(allocated <= arrays + 16 * 1024,
            $"generation allocated {allocated:N0} B; its output arrays are {arrays:N0} B (budget: + 16 KB)");

        TreeMeshData[] Run()
        {
            generator.GrowSkeleton(parameters, parameters.Seed, skeleton);
            var result = new TreeMeshData[lods.Length];
            for (var i = 0; i < lods.Length; i++)
                result[i] = generator.Mesh(skeleton, parameters, lods[i]);
            return result;
        }
    }

    [Fact]
    public void ReportsGenerationTime()
    {
        // Not a gate (timing varies with the machine and the test load): the numbers for the docs.
        var generator = new TreeGenerator();
        var skeleton = new TreeSkeleton();
        foreach (var name in new[] { "Oak Medium", "Ash Large", "Pine Medium" })
        {
            var parameters = Preset(name);
            foreach (var style in new[] { TreeStyle.Realistic, TreeStyle.LowPoly })
            {
                generator.Generate(parameters, style, skeleton); // warm up
                const int runs = 5;
                var watch = Stopwatch.StartNew();
                TreeMeshData[] lods = [];
                for (var i = 0; i < runs; i++)
                    lods = generator.Generate(parameters, style, skeleton);
                var ms = watch.Elapsed.TotalMilliseconds / runs;
                output.WriteLine($"{name} {style}: {ms:F2} ms for the skeleton and 3 LODs; LOD0 {lods[0].VertexCount} vertices, {lods[0].TriangleCount} triangles");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Vertex data
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Presets))]
    public void Custom0CarriesWindWeightLevelPhaseAndOcclusion(string name)
    {
        var parameters = Preset(name);
        var generator = new TreeGenerator();
        var skeleton = generator.GrowSkeleton(parameters, parameters.Seed);
        var mesh = generator.Mesh(skeleton, parameters, new TreeMeshDetail());

        Assert.Equal(mesh.Bark.VertexCount, mesh.Bark.Custom0.Length);
        Assert.Equal(mesh.Leaves.VertexCount, mesh.Leaves.Custom0.Length);
        Assert.Empty(mesh.Bark.Colors);

        // The trunk base does not move; weights stay in 0..1; bark levels are level / 4 (at most 0.75).
        Assert.Equal(0f, mesh.Bark.Custom0[0].X);
        foreach (var c in mesh.Bark.Custom0)
        {
            Assert.InRange(c.X, 0f, 1f);
            Assert.InRange(c.Y, 0f, 0.75f);
            Assert.InRange(c.Z, 0f, 1f);
            Assert.InRange(c.W, 0.6f, 1f);
        }

        // Leaves are marked y = 1 (flutter on), sit on the outer branches, and darken towards the canopy centre.
        foreach (var c in mesh.Leaves.Custom0)
        {
            Assert.Equal(1f, c.Y);
            Assert.InRange(c.X, 0f, 1f);
            Assert.InRange(c.W, 0.4f, 1f);
        }

        // Weights rise monotonically along every branch, from the parent's weight at the attachment point.
        foreach (var branch in skeleton.Branches)
        {
            Assert.True(branch.WeightTip >= branch.WeightBase, $"{name}: a branch's wind weight falls towards its tip");
            if (branch.Parent >= 0)
            {
                var parent = skeleton.Branches[branch.Parent];
                var atAttachment = parent.WeightBase + (parent.WeightTip - parent.WeightBase) * branch.ParentStart;
                Assert.Equal(atAttachment, branch.WeightBase, 1e-12);
            }
        }

        // The tips of the outermost branches reach 1.
        Assert.Equal(1f, mesh.Bark.Custom0.Max(c => c.X), 1e-6f);
    }

    [Fact]
    public void ContinuousBarkUvsRunAlongTheBranchAndEzTreeUvsPingPong()
    {
        var options = TreePresets.Load("Oak Medium");
        var generator = new TreeGenerator();

        options.BarkUv = BarkUvMode.EzTree;
        var ez = generator.Generate(options.ToParams())[0].Bark;
        Assert.All(ez.UVs, uv => Assert.True(uv.Y is 0f or 1f));

        options.BarkUv = BarkUvMode.Continuous;
        var continuous = generator.Generate(options.ToParams())[0].Bark;
        Assert.Equal(ez.Positions, continuous.Positions);
        Assert.Equal(ez.UVs.Select(uv => uv.X), continuous.UVs.Select(uv => uv.X));
        Assert.True(continuous.UVs.Max(uv => uv.Y) > 2, "continuous V should keep growing up the tree");
    }

    [Fact]
    public void LeafCardUvsHaveATopLeftOrigin()
    {
        var mesh = new TreeGenerator().Generate(Preset("Oak Medium"))[0];
        // The quad's first corner is its tip (L along the leaf from its origin): V = 0 there in the engine.
        Assert.Equal(new Vector2(0, 0), mesh.Leaves.UVs[0]);
        Assert.Equal(new Vector2(0, 1), mesh.Leaves.UVs[1]);
    }

    [Fact]
    public void ScaleMultipliesPositionsOnly()
    {
        var options = TreePresets.Load("Aspen Medium");
        options.Scale = 1;
        var unit = new TreeGenerator().Generate(options.ToParams())[0];
        options.Scale = 0.3;
        var scaled = new TreeGenerator().Generate(options.ToParams())[0];

        Assert.Equal(unit.Bark.Normals, scaled.Bark.Normals);
        Assert.Equal(unit.Bark.Indices, scaled.Bark.Indices);
        for (var i = 0; i < unit.Bark.VertexCount; i += 37)
        {
            Assert.Equal(unit.Bark.Positions[i].X * 0.3f, scaled.Bark.Positions[i].X, 1e-4f);
            Assert.Equal(unit.Bark.Positions[i].Y * 0.3f, scaled.Bark.Positions[i].Y, 1e-4f);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Levels of detail
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Presets))]
    public void LevelsOfDetailShrinkAndTheirErrorsRise(string name)
    {
        var parameters = Preset(name);
        foreach (var style in new[] { TreeStyle.Realistic, TreeStyle.LowPoly })
        {
            var lods = new TreeGenerator().Generate(parameters, style);
            Assert.Equal(3, lods.Length);
            for (var i = 1; i < lods.Length; i++)
            {
                Assert.True(lods[i].TriangleCount <= lods[i - 1].TriangleCount, $"{name} {style}: LOD{i} has more triangles than LOD{i - 1}");
                Assert.True(lods[i].GeometricError >= lods[i - 1].GeometricError, $"{name} {style}: LOD{i} error {lods[i].GeometricError} < LOD{i - 1} {lods[i - 1].GeometricError}");
            }

            if (style == TreeStyle.Realistic)
            {
                Assert.Equal(0, lods[0].GeometricError);
                Assert.True(lods[1].GeometricError > 0);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // LowPoly
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Presets))]
    public void LowPolyIsFacetedPaletteGeometryWithinBudget(string name)
    {
        var options = TreePresets.Load(name);
        var parameters = options.ToParams();
        var lods = new TreeGenerator().Generate(parameters, TreeStyle.LowPoly);
        var lod0 = lods[0];

        foreach (var surface in new[] { lod0.Bark, lod0.Leaves })
        {
            // Split normals: three vertices per triangle, all with the face normal, facing the way the triangle winds.
            Assert.Equal(surface.VertexCount, surface.Indices.Length);
            for (var t = 0; t < surface.Indices.Length; t += 3)
            {
                var a = surface.Positions[surface.Indices[t]];
                var b = surface.Positions[surface.Indices[t + 1]];
                var c = surface.Positions[surface.Indices[t + 2]];
                var n = surface.Normals[surface.Indices[t]];
                Assert.Equal(n, surface.Normals[surface.Indices[t + 1]]);
                Assert.Equal(n, surface.Normals[surface.Indices[t + 2]]);
                Assert.Equal(1f, n.Length(), 1e-3f);
                Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), n) > 0, $"{name}: triangle {t / 3} winds against its normal");
            }
        }

        // Blobs: palette shades within ±8 %, foliage marked, at most MaxBlobs (one shade per blob).
        Assert.Equal(lod0.Leaves.VertexCount, lod0.Leaves.Colors.Length);
        Assert.All(lod0.Leaves.Colors, c => Assert.InRange(c.X, 0.92f, 1.08f));
        Assert.All(lod0.Leaves.Custom0, c => Assert.Equal(1f, c.Y));
        var blobs = lod0.Leaves.Custom0.Select(c => c.Z).Distinct().Count();
        Assert.InRange(blobs, 1, options.MaxBlobs);
        Assert.True(lods[2].Leaves.Custom0.Select(c => c.Z).Distinct().Count() <= Math.Max(1, options.MaxBlobs / 4));

        // A low-poly budget: at most 80 triangles per blob, and under half the Realistic triangles (Oak Medium: about
        // 3 000 against 13 806).
        var realistic = new TreeGenerator().Generate(parameters)[0].TriangleCount;
        output.WriteLine($"{name} LowPoly: {lods[0].TriangleCount} / {lods[1].TriangleCount} / {lods[2].TriangleCount} triangles ({lod0.Bark.TriangleCount} bark), {blobs} blobs; Realistic {realistic}");
        Assert.True(lod0.Leaves.TriangleCount <= blobs * 80, $"{name}: {lod0.Leaves.TriangleCount} blob triangles for {blobs} blobs");
        Assert.True(lod0.TriangleCount * 2 < realistic, $"{name}: LowPoly LOD0 has {lod0.TriangleCount} triangles, Realistic {realistic}");
    }

    [Fact]
    public void LowPolyDropsTrianglesBuriedInsideOtherBlobs()
    {
        var parameters = Preset("Oak Medium");
        parameters.BlobJitter = 0;
        var mesh = new TreeGenerator().Mesh(new TreeGenerator().GrowSkeleton(parameters, parameters.Seed), parameters,
            new TreeMeshDetail { Style = TreeStyle.LowPoly, BlobDetail = 1, MaxBlobs = parameters.MaxBlobs });
        var blobs = mesh.Leaves.Custom0.Select(c => c.Z).Distinct().Count();
        // Without the culling every blob would contribute its 80 triangles.
        Assert.True(mesh.Leaves.TriangleCount < blobs * 80, $"{mesh.Leaves.TriangleCount} triangles for {blobs} blobs: nothing was culled");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Trunk, conversion
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Presets))]
    public void TrunkCapsuleFitsTheTrunk(string name)
    {
        var parameters = Preset(name);
        var mesh = new TreeGenerator().Generate(parameters)[0];
        var trunk = mesh.Trunk;
        Assert.True(trunk.Radius > 0, $"{name}: radius {trunk.Radius}");
        Assert.True(trunk.Height >= 2 * trunk.Radius, $"{name}: height {trunk.Height} < diameter");
        Assert.True(trunk.Radius <= parameters.Radius[0] * parameters.Scale + 1e-4, $"{name}: radius {trunk.Radius} wider than the trunk base");
        var top = mesh.Bark.Positions.Max(p => p.Y);
        Assert.True(trunk.Height <= top, $"{name}: capsule {trunk.Height} m taller than the tree ({top} m)");
        output.WriteLine($"{name}: trunk capsule {trunk.Height:F2} m × r {trunk.Radius:F2} m, tree {top:F1} m");
    }

    [Fact]
    public void ToArrayMeshBuildsValidSurfaces()
    {
        var mesh = new TreeGenerator().Generate(Preset("Pine Small"))[0];
        var arrayMesh = mesh.ToArrayMesh();
        Assert.Equal(2, arrayMesh.SurfaceCount);
        for (var i = 0; i < arrayMesh.SurfaceCount; i++)
            arrayMesh.GetSurface(i).Validate();
        Assert.Same(mesh.Bark.Positions, arrayMesh.GetSurface(0).Positions);
        Assert.Equal(mesh.Leaves.TriangleCount, arrayMesh.GetSurface(1).IndexCount / 3);

        var parameters = Preset("Pine Small");
        parameters.LeafCount = 0;
        var bare = new TreeGenerator().Generate(parameters)[0];
        Assert.Equal(0, bare.Leaves.VertexCount);
        Assert.Equal(1, bare.ToArrayMesh().SurfaceCount);
    }

    [Fact]
    public void InvalidParametersAreRejected()
    {
        var parameters = TreeParams.EzTreeDefaults();
        parameters.Levels = 4;
        Assert.Throws<ArgumentException>(() => new TreeGenerator().GrowSkeleton(parameters, 0));
        parameters = TreeParams.EzTreeDefaults();
        parameters.Length = [1, 2];
        Assert.Throws<ArgumentException>(() => new TreeGenerator().GrowSkeleton(parameters, 0));
    }

    [Fact]
    public void TrunkOnlyTreeGrowsLeavesOnTheTrunk()
    {
        var parameters = TreeParams.EzTreeDefaults();
        parameters.Levels = 0;
        parameters.LeafCount = 5;
        var skeleton = new TreeGenerator().GrowSkeleton(parameters, 3);
        Assert.Equal(1, skeleton.BranchCount);
        Assert.Equal(6, skeleton.LeafCount); // the terminal leaf plus five along the trunk
    }

    private static long ArrayBytes(TreeSurfaceData s) =>
        Size(s.Positions) + Size(s.Normals) + Size(s.UVs) + Size(s.Custom0) + Size(s.Colors) + Size(s.Indices);

    private static long Size<T>(T[] array) where T : unmanaged => array.Length == 0 ? 0 : 24 + (long)array.Length * Marshal.SizeOf<T>();

    private static byte[] Bytes(TreeMeshData[] lods)
    {
        using var stream = new MemoryStream();
        foreach (var lod in lods)
        {
            foreach (var s in new[] { lod.Bark, lod.Leaves })
            {
                stream.Write(MemoryMarshal.AsBytes(s.Positions.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.Normals.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.UVs.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.Custom0.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.Colors.AsSpan()));
                stream.Write(MemoryMarshal.AsBytes(s.Indices.AsSpan()));
            }

            stream.Write(BitConverter.GetBytes(lod.GeometricError));
        }

        return stream.ToArray();
    }
}
