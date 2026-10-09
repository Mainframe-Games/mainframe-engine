using System.Numerics;

namespace MainframeEngine.Tests.Lighting;

/// <summary>ADR 0170: the light-probe math, the bake on analytic scenes, the fix-up, determinism and the data format.</summary>
public class LightProbeTests
{
    private const int Per = LightProbeData.CoefficientsPerProbe;

    // ── Spherical harmonics ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L1IrradianceOfAUniformFunctionIsTheFunction()
    {
        var c0 = 4f * MathF.PI * SphericalHarmonics.Y0 * 0.7f;
        foreach (var n in new[] { Vector3.UnitX, -Vector3.UnitY, Vector3.Normalize(new Vector3(1, 2, 3)) })
            Assert.Equal(0.7f, SphericalHarmonics.L1Irradiance(c0, Vector3.Zero, n), 5);
    }

    [Fact]
    public void L1ProjectionOfTheUpperHemisphereMatchesTheCosineIntegral()
    {
        // Project the upper-hemisphere indicator numerically, then compare the cosine convolution with the exact one:
        // a normal tilted θ from up sees (1 + cos θ) / 2 of the cosine lobe in the upper hemisphere.
        const int samples = 20000;
        float c0 = 0f;
        var c1 = Vector3.Zero;
        for (var i = 0; i < samples; i++)
        {
            var d = SphericalHarmonics.FibonacciDirection(i, samples);
            if (d.Y <= 0f)
                continue;
            c0 += SphericalHarmonics.Y0;
            c1 += SphericalHarmonics.Y1 * d;
        }

        c0 *= 4f * MathF.PI / samples;
        c1 *= 4f * MathF.PI / samples;
        foreach (var theta in new[] { 0f, 0.5f, 1.2f, MathF.PI / 2, 2.5f, MathF.PI })
        {
            var n = new Vector3(MathF.Sin(theta), MathF.Cos(theta), 0f);
            var exact = (1f + MathF.Cos(theta)) * 0.5f;
            Assert.InRange(SphericalHarmonics.L1Irradiance(c0, c1, n), exact - 0.01f, exact + 0.01f);
        }
    }

    [Fact]
    public void L2IrradianceMatchesANumericCosineIntegral()
    {
        Vector3 Sky(Vector3 d) => new(0.2f + 0.8f * MathF.Max(d.Y, 0f), 0.4f + 0.3f * d.X, 0.5f);
        var sh = ShL2Rgb.Project(Sky, 4096);
        foreach (var n in new[] { Vector3.UnitY, Vector3.UnitX, Vector3.Normalize(new Vector3(-1, -1, 2)) })
        {
            // ∫ L(ω) max(n·ω, 0) dω / π, numerically.
            var sum = Vector3.Zero;
            const int samples = 40000;
            for (var i = 0; i < samples; i++)
            {
                var d = SphericalHarmonics.FibonacciDirection(i, samples);
                sum += Sky(d) * MathF.Max(Vector3.Dot(n, d), 0f);
            }

            var exact = sum * (4f * MathF.PI / samples) / MathF.PI;
            var approx = sh.Irradiance(n);
            Assert.True(Vector3.Distance(exact, approx) < 0.03f * exact.Length(), $"n {n}: {approx} vs {exact}");
        }
    }

    // ── Bakes on analytic scenes ─────────────────────────────────────────────────────────────────────────────────

    private static ProbeBakeResult Bake(ProbeBakeScene scene, ProbeGrid grid, ProbeBakeSettings settings) =>
        ProbeBaker.Bake(scene, grid, settings, null, TestContext.Current.CancellationToken);

    private static ProbeGrid OneProbe(Vector3 at) => new(ProbeLayout.Box, at, Vector3.One, 1, 1, 1, []);

    private static void AddQuad(ProbeBakeSceneBuilder builder, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 albedo) =>
        builder.AddTriangles([a, b, c, d], [0, 1, 2, 0, 2, 3], albedo);

    // A floor at y = 0 facing up (counter-clockwise seen from above), size ±half.
    private static void AddFloor(ProbeBakeSceneBuilder builder, float half, Vector3 albedo) =>
        AddQuad(builder, new(-half, 0, -half), new(-half, 0, half), new(half, 0, half), new(half, 0, -half), albedo);

    private static float MeanVisibility(LightProbeData data, int probe = 0) => data.Coefficients[probe * Per] * SphericalHarmonics.Y0;

    [Fact]
    public void AProbeOverAnInfinitePlaneSeesHalfTheSky()
    {
        var builder = new ProbeBakeSceneBuilder();
        AddFloor(builder, 5000f, new Vector3(0.5f));
        var result = Bake(builder.Build(), OneProbe(new Vector3(0, 1, 0)), new ProbeBakeSettings { RaysPerProbe = 1024, Bounces = 0, Blur = false });
        var c = result.Data.Coefficients;
        Assert.InRange(MeanVisibility(result.Data), 0.49f, 0.51f);
        Assert.InRange(SphericalHarmonics.L1Irradiance(c[0], new Vector3(c[1], c[2], c[3]), Vector3.UnitY), 0.97f, 1.03f);
        Assert.InRange(SphericalHarmonics.L1Irradiance(c[0], new Vector3(c[1], c[2], c[3]), -Vector3.UnitY), -0.03f, 0.03f);
        Assert.Equal(0, result.InvalidProbes);
    }

    // A quad whose front (counter-clockwise winding) faces `facing`.
    private static void AddQuadFacing(ProbeBakeSceneBuilder builder, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 facing, Vector3 albedo)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), facing) < 0f)
            (b, d) = (d, b);
        AddQuad(builder, a, b, c, d, albedo);
    }

    // An axis-aligned box: faces pointing out (a solid block) or in (a room).
    private static void AddBox(ProbeBakeSceneBuilder builder, Vector3 centre, Vector3 half, bool inward, Vector3 albedo)
    {
        Vector3 P(int x, int y, int z) => centre + new Vector3(x * half.X, y * half.Y, z * half.Z);
        var s = inward ? -1f : 1f;
        AddQuadFacing(builder, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), -Vector3.UnitY * s, albedo);
        AddQuadFacing(builder, P(-1, 1, -1), P(1, 1, -1), P(1, 1, 1), P(-1, 1, 1), Vector3.UnitY * s, albedo);
        AddQuadFacing(builder, P(-1, -1, -1), P(-1, 1, -1), P(-1, 1, 1), P(-1, -1, 1), -Vector3.UnitX * s, albedo);
        AddQuadFacing(builder, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), Vector3.UnitX * s, albedo);
        AddQuadFacing(builder, P(-1, -1, -1), P(1, -1, -1), P(1, 1, -1), P(-1, 1, -1), -Vector3.UnitZ * s, albedo);
        AddQuadFacing(builder, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), Vector3.UnitZ * s, albedo);
    }

    private static void AddRoom(ProbeBakeSceneBuilder builder, float h, Vector3 albedo) =>
        AddBox(builder, Vector3.Zero, new Vector3(h), inward: true, albedo);

    [Fact]
    public void AProbeInAClosedRoomSeesNoSky()
    {
        var builder = new ProbeBakeSceneBuilder();
        AddRoom(builder, 2f, new Vector3(0.5f));
        var result = Bake(builder.Build(), OneProbe(new Vector3(0.1f, 0.2f, -0.3f)), new ProbeBakeSettings { RaysPerProbe = 512, Bounces = 0, Blur = false });
        Assert.InRange(MeanVisibility(result.Data), -1e-4f, 1e-4f);
        Assert.Equal(0, result.InvalidProbes);
    }

    [Fact]
    public void ASunlitWhiteFloorBouncesItsRadianceUpwards()
    {
        // Engine units: a sun of intensity E lights a white floor to radiance ρ·E (Lambert's 1/π is in the light), so a
        // probe above it sees ρ·E from the whole lower hemisphere: an ambient value of ρ·E facing down.
        const float e = 2f, rho = 0.6f;
        var builder = new ProbeBakeSceneBuilder { SunDirection = Vector3.UnitY, SunRadiance = new Vector3(e) };
        AddFloor(builder, 5000f, new Vector3(rho));
        var result = Bake(builder.Build(), OneProbe(new Vector3(0, 1, 0)), new ProbeBakeSettings { RaysPerProbe = 256, BounceRays = 2048, Bounces = 1, Blur = false });
        var c = result.Data.Coefficients;
        var down = SphericalHarmonics.L1Irradiance(c[4], new Vector3(c[5], c[6], c[7]), -Vector3.UnitY);
        var up = SphericalHarmonics.L1Irradiance(c[4], new Vector3(c[5], c[6], c[7]), Vector3.UnitY);
        Assert.InRange(down, rho * e * 0.97f, rho * e * 1.03f);
        Assert.InRange(up, -0.03f, 0.03f);
        // Grey floor, grey bounce.
        Assert.Equal(c[4], c[8], 3);
        Assert.Equal(c[4], c[12], 3);
    }

    [Fact]
    public void ASkyLitFloorBouncesTheSkyItSees()
    {
        // A uniform sky of radiance S over a floor: the floor's ambient is S × its visibility (1 facing up), so it sends
        // ρ·S up: the probe's bounce facing down is ρ·S (needs the visibility pass first: the second bounce pass sees it).
        const float s = 0.8f, rho = 0.5f;
        var builder = new ProbeBakeSceneBuilder { Sky = ShL2Rgb.Uniform(new Vector3(s)) };
        AddFloor(builder, 5000f, new Vector3(rho));
        var result = Bake(builder.Build(), OneProbe(new Vector3(0, 0.5f, 0)), new ProbeBakeSettings { RaysPerProbe = 512, BounceRays = 2048, Bounces = 1, Blur = false });
        var c = result.Data.Coefficients;
        var down = SphericalHarmonics.L1Irradiance(c[4], new Vector3(c[5], c[6], c[7]), -Vector3.UnitY);
        Assert.InRange(down, rho * s * 0.95f, rho * s * 1.05f);
    }

    [Fact]
    public void TheBakeIsDeterministicAcrossThreadCounts()
    {
        var builder = new ProbeBakeSceneBuilder { SunDirection = Vector3.Normalize(new Vector3(0.3f, 1f, 0.2f)), SunRadiance = new Vector3(1.5f), Sky = ShL2Rgb.Uniform(new Vector3(0.3f)) };
        AddFloor(builder, 100f, new Vector3(0.4f));
        AddRoom(builder, 1f, new Vector3(0.7f, 0.2f, 0.2f));
        var scene = builder.Build();
        var grid = new ProbeGrid(ProbeLayout.Box, new Vector3(-4, 0.5f, -4), new Vector3(2), 5, 2, 5, []);
        var settings = new ProbeBakeSettings { RaysPerProbe = 64, Bounces = 2, Seed = 7 };
        var one = Bake(scene, grid, settings with { MaxThreads = 1 });
        var many = Bake(scene, grid, settings with { MaxThreads = 8 });
        Assert.Equal(one.Data.Coefficients.ToArray(), many.Data.Coefficients.ToArray());
        Assert.Equal(one.Data.BakeHash, many.Data.BakeHash);
        var other = Bake(scene, grid, settings with { Seed = 8 });
        Assert.NotEqual(one.Data.Coefficients.ToArray(), other.Data.Coefficients.ToArray());
    }

    [Fact]
    public void ProbesInsideGeometryAreReplacedByTheirNeighbours()
    {
        // A solid block (outward faces) swallows the middle probe of a row: its rays hit back faces, so it is invalid and
        // takes its neighbours' values instead of a black, sky-less probe that would leak darkness around the block.
        var builder = new ProbeBakeSceneBuilder();
        AddFloor(builder, 1000f, new Vector3(0.5f));
        AddBox(builder, new Vector3(0, 1, 0), new Vector3(0.5f), inward: false, new Vector3(0.5f));
        var grid = new ProbeGrid(ProbeLayout.Box, new Vector3(-3, 1, 0), new Vector3(3, 1, 1), 3, 1, 1, []);
        var result = ProbeBaker.Bake(builder.Build(), grid, new ProbeBakeSettings { RaysPerProbe = 256, Bounces = 0, Blur = false }, null, TestContext.Current.CancellationToken, out var valid);
        Assert.Equal([true, false, true], valid);
        Assert.Equal(1, result.InvalidProbes);
        var c = result.Data.Coefficients;
        for (var k = 0; k < 4; k++)
            Assert.Equal((c[k] + c[2 * Per + k]) * 0.5f, c[Per + k], 4);
    }

    [Fact]
    public void DilationGrowsInwardsAndFallsBackToOpenSky()
    {
        var grid = new ProbeGrid(ProbeLayout.Box, Vector3.Zero, Vector3.One, 4, 1, 1, []);
        var c = new float[4 * Per];
        c[0] = 1f;
        ProbeBaker.Dilate(grid, c, [true, false, false, false], 0, 4, _ => 9f);
        Assert.Equal([1f, 1f, 1f, 1f], new[] { c[0], c[Per], c[2 * Per], c[3 * Per] });
        var empty = new float[2 * Per];
        ProbeBaker.Dilate(new ProbeGrid(ProbeLayout.Box, Vector3.Zero, Vector3.One, 2, 1, 1, []), empty, [false, false], 0, 4, k => k);
        Assert.Equal([0f, 1f, 2f, 3f], empty.AsSpan(Per, 4).ToArray());
    }

    [Fact]
    public void CanopyTransmittanceFollowsBeerLambert()
    {
        // A horizontal layer of leaf cards (area density a per m³ in a 2 m slab): straight down, T = exp(−G · a · 2).
        var positions = new List<Vector3>();
        var indices = new List<int>();
        var rng = new Random(3);
        const int cards = 4000;
        const float side = 0.1f; // 0.01 m² each
        for (var i = 0; i < cards; i++)
        {
            var c = new Vector3((float)rng.NextDouble() * 10f - 5f, 4f + (float)rng.NextDouble() * 2f, (float)rng.NextDouble() * 10f - 5f);
            var b = positions.Count;
            positions.AddRange([c, c + new Vector3(side, 0, 0), c + new Vector3(side, 0, side), c + new Vector3(0, 0, side)]);
            indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }

        var grid = new ProbeLeafGrid(positions.ToArray(), indices.ToArray(), 1f, 0.5f);
        var density = cards * side * side / (10f * 10f * 2f);
        Assert.InRange(grid.TotalExtinctionVolume, 0.97f * ProbeLeafGrid.G * cards * side * side, 1.03f * ProbeLeafGrid.G * cards * side * side);
        float tau = 0f, eventT = 0f;
        var expected = MathF.Exp(-ProbeLeafGrid.G * density * 2f);
        var sum = 0f;
        for (var i = 0; i < 64; i++)
        {
            tau = 0f;
            var x = -3f + 6f * (i % 8) / 7f;
            var z = -3f + 6f * (i / 8) / 7f;
            grid.March(new Vector3(x, 10f, z), -Vector3.UnitY, 0f, 20f, ref tau, float.PositiveInfinity, ref eventT);
            sum += MathF.Exp(-tau);
        }

        Assert.InRange(sum / 64f, expected * 0.85f, expected * 1.15f);
    }

    [Fact]
    public void TerrainFollowingLayersArePiecewiseLinear()
    {
        var grid = new ProbeGrid(ProbeLayout.TerrainFollowing, Vector3.Zero, new Vector3(2), 2, 4, 2, [0.5f, 1.5f, 3.5f, 7.5f]);
        Assert.Equal(0f, grid.LayerCoordinate(0.1f));
        Assert.Equal(0f, grid.LayerCoordinate(0.5f));
        Assert.Equal(0.5f, grid.LayerCoordinate(1f), 5);
        Assert.Equal(1.5f, grid.LayerCoordinate(2.5f), 5);
        Assert.Equal(3f, grid.LayerCoordinate(100f));
        Assert.Equal(new Vector3(2, 13.5f, 0), grid.Position(1, 2, 0, 10f));
    }

    [Fact]
    public void ADataFileRoundTripsAndPacksTheTexture()
    {
        var grid = new ProbeGrid(ProbeLayout.TerrainFollowing, new Vector3(1, 0, 2), new Vector3(2), 3, 2, 2, [0.5f, 2f]);
        var coefficients = new float[grid.ProbeCount * Per];
        for (var i = 0; i < coefficients.Length; i++)
            coefficients[i] = (i % 97) * 0.125f;
        var ground = new float[grid.ColumnCount];
        for (var i = 0; i < ground.Length; i++)
            ground[i] = 10f + i;
        var data = new LightProbeData { BakeHash = "abc" };
        data.SetData(grid, coefficients, ground);

        var path = Path.Combine(Path.GetTempPath(), $"probes-{Guid.NewGuid():N}.probes");
        try
        {
            data.WriteDataFile(path);
            var copy = new LightProbeData { Layout = grid.Layout, CountX = 3, CountY = 2, CountZ = 2, LayerHeights = [0.5f, 2f] };
            copy.ReadDataFile(path);
            Assert.Equal(coefficients, copy.Coefficients.ToArray()); // multiples of 1/8 below 16 are exact in half floats
            Assert.Equal(ground, copy.Ground.ToArray());
        }
        finally
        {
            File.Delete(path);
        }

        var texture = data.ToTexture()!;
        Assert.Equal((3, 2 * ProbeGrid.Slabs, 2), (texture.Width, texture.Height, texture.Depth));
        // Probe (2, 1, 1): sky slab at y = 1, bounce blue slab at y = 3 · 2 + 1, ground slab at y = 4 · 2 + 1.
        var probe = grid.Index(2, 1, 1) * Per;
        Assert.Equal(new Vector4(coefficients[probe], coefficients[probe + 1], coefficients[probe + 2], coefficients[probe + 3]), texture.GetTexel(2, 1, 1));
        Assert.Equal(new Vector4(coefficients[probe + 12], coefficients[probe + 13], coefficients[probe + 14], coefficients[probe + 15]), texture.GetTexel(2, 7, 1));
        Assert.Equal(ground[grid.Column(2, 1)], texture.GetTexel(2, 9, 1).X);
    }

    [Fact]
    public void GIModeDefaultsToStatic() => Assert.Equal(GIMode.Static, new MeshInstance3D().GIMode);

    // ── Proxies and hashing ──────────────────────────────────────────────────────────────────────────────────────

    private static Terrain3D RollingTerrain()
    {
        var data = new TerrainData { SizeMeters = 32, VertexSpacing = 0.5f, ChunkMeters = 8, HeightMin = -8f, HeightMax = 24f };
        var terrain = new Terrain3D { Data = data, Position = new Vector3(-16f, 0f, -16f) };
        data.EnsureLoaded();
        terrain.SetHeightsFrom((x, z) => 2f + 1.5f * MathF.Sin(x * 0.4f) * MathF.Cos(z * 0.3f) + 0.2f * MathF.Sin(x * 3.1f + z * 1.7f));
        return terrain;
    }

    [Fact]
    public void TheHeightfieldPyramidMatchesEveryQuadTested()
    {
        var data = RollingTerrain().Data!;
        var grid = data.Grid;
        var heights = data.BedHeights.ToArray();
        var field = new ProbeHeightfield(grid, heights);
        var rng = new Random(11);
        var hits = 0;
        for (var r = 0; r < 400; r++)
        {
            var origin = new Vector3((float)rng.NextDouble() * 40f - 4f, (float)rng.NextDouble() * 8f - 1f, (float)rng.NextDouble() * 40f - 4f);
            var dir = Vector3.Normalize(new Vector3((float)rng.NextDouble() * 2f - 1f, (float)rng.NextDouble() * 1.2f - 0.7f, (float)rng.NextDouble() * 2f - 1f));
            var fast = field.Raycast(origin, dir, 100f, out var t, out _);
            // Brute force: every quad's two triangles.
            var best = float.MaxValue;
            for (var j = 0; j < grid.Quads; j++)
                for (var i = 0; i < grid.Quads; i++)
                    if (grid.RaycastQuad(heights, i, j, origin, dir, out var d, out _) && d < best && d <= 100f)
                        best = d;
            Assert.Equal(best < float.MaxValue, fast);
            if (fast)
            {
                hits++;
                Assert.Equal(best, t, 3);
            }
        }

        Assert.InRange(hits, 100, 399); // both outcomes covered
    }

    [Fact]
    public void TheFingerprintIsTheBuiltScenesContentHash()
    {
        var tree = TreeMesh.Generate(TreePresets.Load("Bush 1"), 2, MainframeEngine.Trees.TreeStyle.Realistic);
        ProbeBakeSceneBuilder Fill(bool fingerprint, float x)
        {
            var builder = new ProbeBakeSceneBuilder(fingerprint) { SunDirection = Vector3.UnitY, SunRadiance = Vector3.One };
            builder.AddTerrain(RollingTerrain());
            builder.AddTreeInstance(tree, null, null, Transform3D.FromTrs(new Vector3(x, 2f, 1f), Quaternion.Identity, Vector3.One));
            builder.AddMesh(new BoxMesh { Size = Vector3.One }, null, Transform3D.Identity);
            return builder;
        }

        var built = Fill(false, 3f).Build();
        Assert.Equal(built.ContentHash, Fill(true, 3f).Fingerprint());
        Assert.NotEqual(built.ContentHash, Fill(true, 3.5f).Fingerprint()); // a moved tree is a new scene
        Assert.True(built.HasTerrain);
        Assert.Equal(2, built.InstanceCount);
    }

    [Fact]
    public void FoliageLeavesPassTheirTranslucencyToTheBake()
    {
        var tree = TreeMesh.Generate(TreePresets.Load("Bush 1"), 2, MainframeEngine.Trees.TreeStyle.Realistic);
        ProbeBakeSceneBuilder Fill(bool fingerprint, Material leaves)
        {
            var builder = new ProbeBakeSceneBuilder(fingerprint) { SunDirection = Vector3.UnitY, SunRadiance = Vector3.One };
            builder.AddTreeInstance(tree, null, leaves, Transform3D.Identity);
            return builder;
        }

        var foliage = new FoliageMaterial3D { Translucency = 0.7f };
        Assert.Equal(0.7f, Fill(false, foliage).Build().Leaf(0).Transmission); // what the foliage shader passes
        Assert.Equal(ProbeBakeSceneBuilder.LeafTransmission, Fill(false, new StandardMaterial3D()).Build().Leaf(0).Transmission);
        var before = Fill(true, foliage).Fingerprint();
        foliage.Translucency = 0.5f;
        Assert.NotEqual(before, Fill(true, foliage).Fingerprint()); // a new translucency makes the bake stale
    }

    [Fact]
    public void TheOcclusionTintIsLinearAndWhiteByDefault()
    {
        var volume = new LightProbeVolume();
        Assert.Equal(1f, volume.SkyOcclusion);
        Assert.Equal(Vector3.One, volume.OcclusionTintLinear);
        volume.OcclusionTint = System.Drawing.Color.FromArgb(255, 188, 128, 0);
        var tint = volume.OcclusionTintLinear;
        Assert.Equal(ColorSpace.SrgbToLinear(188 / 255f), tint.X, 5);
        Assert.Equal(ColorSpace.SrgbToLinear(128 / 255f), tint.Y, 5);
        Assert.Equal(0f, tint.Z);
    }

    [Fact]
    public void BakeLightingIsAHostFlag()
    {
        Assert.True(GameHostOptions.Parse(["--bake-lighting", "--headless"]).BakeLighting);
        Assert.False(GameHostOptions.Parse(["++", "--bake-lighting"]).BakeLighting); // the game's own after ++
    }

    [Fact]
    public void ABackgroundBakeLandsOnTheNextProcess()
    {
        var tree = new SceneTree { EditMode = true }; // the volume is a tool: it bakes in the editor too
        var root = new Node3D { Name = "Scene" };
        root.AddChild(new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) }, Position = new Vector3(0f, -3f, 0f) });
        var volume = new LightProbeVolume { Size = new Vector3(4f), ProbeSpacing = new Vector3(2f), RaysPerProbe = 32, Bounces = 1 };
        root.AddChild(volume);
        tree.Root.AddChild(root);
        var baked = 0;
        volume.Baked += () => baked++;
        volume.BakeInBackground();
        for (var i = 0; i < 2000 && volume.Data is null; i++)
        {
            Thread.Sleep(2);
            tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        }

        Assert.NotNull(volume.Data);
        Assert.Equal(1, baked);
        Assert.False(volume.IsBaking);
        tree.Shutdown();
    }

    [Fact]
    public void TheVolumeKnowsWhenItsBakeIsCurrent()
    {
        var root = new Node3D { Name = "Root" };
        var volume = new LightProbeVolume { Size = new Vector3(4f), ProbeSpacing = new Vector3(2f), RaysPerProbe = 32, Bounces = 0 };
        var floor = new MeshInstance3D { Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) }, Position = new Vector3(0f, -3f, 0f) };
        root.AddChild(floor);
        root.AddChild(volume);
        Assert.False(volume.IsBakeCurrent());
        var result = volume.Bake(cancellation: TestContext.Current.CancellationToken);
        Assert.Same(result.Data, volume.Data);
        Assert.Equal(27, result.Probes);
        Assert.True(volume.IsBakeCurrent());
        floor.Position = new Vector3(0f, -2f, 0f);
        Assert.False(volume.IsBakeCurrent());
    }
}
