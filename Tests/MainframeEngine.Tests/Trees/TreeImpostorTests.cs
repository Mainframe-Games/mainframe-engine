using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine.Tests.Trees;

/// <summary>ADR 0172: the octahedral mapping (<see cref="ImpostorOctahedron"/>), the impostor bake and the scatter's impostor level.</summary>
[Collection(nameof(Scene.SerialResources))] // presets and textures go through the process-wide loader
public sealed class TreeImpostorTests
{
    public static TheoryData<bool> Modes() => [true, false];

    [Theory]
    [MemberData(nameof(Modes))]
    public void EncodeAndDecodeRoundTripOverTheGrid(bool hemi)
    {
        for (var j = 0; j <= 16; j++)
        {
            for (var i = 0; i <= 16; i++)
            {
                var grid = new Vector2(i, j) / 16f;
                var direction = ImpostorOctahedron.Decode(grid, hemi);
                Assert.InRange(direction.Length(), 0.9999f, 1.0001f);
                if (hemi)
                    Assert.True(direction.Y >= -1e-6f);
                var back = ImpostorOctahedron.Encode(direction, hemi);
                // The full octahedron's border folds onto itself: compare directions there.
                Assert.True(Vector2.Distance(grid, back) < 1e-4f || Vector3.Distance(direction, ImpostorOctahedron.Decode(back, hemi)) < 1e-4f,
                    $"({i}, {j}): {grid} → {direction} → {back}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void DirectionsSurviveTheRoundTrip(bool hemi)
    {
        var state = 7u;
        float Next()
        {
            state = state * 1664525u + 1013904223u;
            return (state >> 8) / 16777216f * 2f - 1f;
        }

        for (var n = 0; n < 2000; n++)
        {
            var d = Vector3.Normalize(new Vector3(Next(), hemi ? MathF.Abs(Next()) : Next(), Next()) + new Vector3(1e-4f));
            var back = ImpostorOctahedron.Decode(ImpostorOctahedron.Encode(d, hemi), hemi);
            Assert.True(Vector3.Distance(d, back) < 1e-4f, $"{d} → {back}");
        }
    }

    [Fact]
    public void HemiGridLooksDownAtTheCentreAndAlongTheHorizonAtTheBorder()
    {
        Assert.Equal(Vector3.UnitY, ImpostorOctahedron.FrameDirection(4, 4, 9, hemi: true));
        foreach (var (i, j) in ((int, int)[])[(0, 0), (8, 0), (0, 8), (8, 8), (4, 0), (0, 4), (8, 4), (4, 8)])
            Assert.InRange(ImpostorOctahedron.FrameDirection(i, j, 9, hemi: true).Y, -1e-6f, 1e-6f);
        Assert.Equal(Vector3.UnitX, ImpostorOctahedron.FrameDirection(8, 0, 9, hemi: true));
        Assert.Equal(Vector3.UnitZ, ImpostorOctahedron.FrameDirection(8, 8, 9, hemi: true));
        // The full octahedron's corners look straight up from below.
        Assert.True(Vector3.Distance(-Vector3.UnitY, ImpostorOctahedron.FrameDirection(0, 0, 9, hemi: false)) < 1e-5f);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void BlendPicksTheNearestFramesWithWeightsSummingToOne(bool hemi)
    {
        const int frames = 8;
        // On a frame's own direction: that frame alone.
        for (var j = 0; j < frames; j++)
        {
            for (var i = 0; i < frames; i++)
            {
                var (a, b, c) = ImpostorOctahedron.Blend(ImpostorOctahedron.FrameDirection(i, j, frames, hemi), frames, hemi);
                var winner = new[] { a, b, c }.MaxBy(f => f.Weight);
                Assert.InRange(winner.Weight, 0.999f, 1.0001f);
                // The full octahedron's four corners are one direction (straight up from below).
                var expected = ImpostorOctahedron.FrameDirection(i, j, frames, hemi);
                Assert.True(Vector3.Distance(expected, ImpostorOctahedron.FrameDirection(winner.Column, winner.Row, frames, hemi)) < 1e-5f,
                    $"({i}, {j}) blends to ({winner.Column}, {winner.Row})");
            }
        }

        // The widest angle between neighbouring frames (diagonals included).
        var step = 0f;
        for (var j = 0; j < frames - 1; j++)
        {
            for (var i = 0; i < frames - 1; i++)
            {
                var f = ImpostorOctahedron.FrameDirection(i, j, frames, hemi);
                foreach (var (di, dj) in ((int, int)[])[(1, 0), (0, 1), (1, 1)])
                    step = MathF.Max(step, MathF.Acos(Math.Clamp(Vector3.Dot(f, ImpostorOctahedron.FrameDirection(i + di, j + dj, frames, hemi)), -1f, 1f)));
                step = MathF.Max(step, MathF.Acos(Math.Clamp(Vector3.Dot(ImpostorOctahedron.FrameDirection(i + 1, j, frames, hemi),
                    ImpostorOctahedron.FrameDirection(i, j + 1, frames, hemi)), -1f, 1f)));
            }
        }

        var state = 3u;
        for (var n = 0; n < 500; n++)
        {
            state = state * 1664525u + 1013904223u;
            var d = ImpostorOctahedron.Decode(new Vector2((state >> 8) % 1000 / 999f, (state >> 18) % 1000 / 999f), hemi);
            var (a, b, c) = ImpostorOctahedron.Blend(d, frames, hemi);
            Assert.InRange(a.Weight + b.Weight + c.Weight, 0.9999f, 1.0001f);
            foreach (var f in (ImpostorFrame[])[a, b, c])
            {
                Assert.InRange(f.Weight, -1e-6f, 1.0001f);
                Assert.InRange(f.Column, 0, frames - 1);
                Assert.InRange(f.Row, 0, frames - 1);
                // Every blended view is within one grid step of the direction.
                var angle = MathF.Acos(Math.Clamp(Vector3.Dot(d, ImpostorOctahedron.FrameDirection(f.Column, f.Row, frames, hemi)), -1f, 1f));
                Assert.True(angle <= step + 1e-4f, $"frame ({f.Column}, {f.Row}) is {angle} rad from {d} (grid step {step})");
            }
        }
    }

    [Fact]
    public void BasisIsOrthonormalAndRightHanded()
    {
        foreach (var d in (Vector3[])[Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new Vector3(1f, 0.5f, -2f)), -Vector3.UnitY])
        {
            var (right, up) = ImpostorOctahedron.Basis(d);
            Assert.InRange(right.Length(), 0.999f, 1.001f);
            Assert.InRange(up.Length(), 0.999f, 1.001f);
            Assert.InRange(Vector3.Dot(right, up), -1e-5f, 1e-5f);
            Assert.InRange(Vector3.Dot(right, d), -1e-5f, 1e-5f);
            Assert.True(Vector3.Distance(Vector3.Cross(right, up), d) < 1e-4f);
        }
    }

    [Fact]
    public void BakeIsDeterministicAndCoversEveryView()
    {
        var mesh = TreeMesh.Generate(TreePresets.Load("Aspen Small"), 271, TreeStyle.Realistic);
        var options = TreePresets.Load("Aspen Small");
        var bark = TreeMaterials.Bark(options, TreeStyle.Realistic);
        var leaves = TreeMaterials.Leaves(options, TreeStyle.Realistic);
        var settings = new TreeImpostorOptions { Frames = 4, CellSize = 32 };
        var first = TreeImpostorBaker.Bake(mesh.Lods[0], bark, leaves, settings);
        var second = TreeImpostorBaker.Bake(mesh.Lods[0], bark, leaves, settings);

        Assert.Equal(first.AlbedoPixels, second.AlbedoPixels);
        Assert.Equal(first.NormalPixels, second.NormalPixels);
        Assert.Equal(first.DetailPixels, second.DetailPixels);
        Assert.Equal(64 * 64 * 4, first.DetailPixels.Length); // half resolution

        // The sphere holds the mesh; every view sees the tree, with object-space normals (unit) and leaves masked.
        var bounds = mesh.Lods[0].GetSurface(0).Bounds.Merge(mesh.Lods[0].GetSurface(1).Bounds);
        Assert.True(Vector3.Distance(bounds.Center, first.Center) < 1e-3f);
        foreach (var surface in mesh.Lods[0].Surfaces)
            Assert.All(surface.Positions, p => Assert.True(Vector3.Distance(p, first.Center) <= first.Radius));
        var leafTexels = 0;
        for (var cell = 0; cell < 16; cell++)
        {
            int x0 = cell % 4 * 32, y0 = cell / 4 * 32, covered = 0;
            for (var y = y0; y < y0 + 32; y++)
            {
                for (var x = x0; x < x0 + 32; x++)
                {
                    var i = (y * 128 + x) * 4;
                    if (first.AlbedoPixels[i + 3] < 128)
                        continue;
                    covered++;
                    var n = new Vector3(first.NormalPixels[i], first.NormalPixels[i + 1], first.NormalPixels[i + 2]) / 127.5f - Vector3.One;
                    Assert.InRange(n.Length(), 0.9f, 1.1f);
                    if (first.DetailPixels[(y / 2 * 64 + x / 2) * 4 + 2] > 128)
                        leafTexels++;
                }
            }

            Assert.True(covered > 20, $"view {cell} covers {covered} texels");
        }

        Assert.True(leafTexels > 0);
    }

    [Fact]
    public void ScatterAddsAnImpostorLevelAndPerInstanceRanges()
    {
        var scatter = new TreeScatter
        {
            Species = [new TreeSpecies { Preset = "Aspen Small", Seeds = [271] }],
            Lod1Distance = 20f,
            Lod2Distance = 40f,
            ImpostorDistance = 70f,
            MaxDistance = 300f,
            ImpostorFrames = 4,
            ImpostorResolution = 32,
            LodSelection = TreeLodSelection.PerInstance,
            LodFadeMargin = 2f,
            Collision = false,
        };
        scatter.SetPlacements([new TreePlacement(Vector3.Zero, 0f, 1f, 0), new TreePlacement(new Vector3(10f, 0f, 3f), 1f, 1f, 0)]);
        scatter.Rebuild();
        try
        {
            Assert.Equal(4, scatter.Batches.Count);
            var impostor = Assert.Single(scatter.Batches, b => b.IsImpostor);
            Assert.Equal(3, impostor.Lod);
            Assert.All(scatter.Batches, b => Assert.True(b.HasImpostorLevel));
            Assert.Equal((0f, 20f), scatter.LevelRange(0, 4, impostor: true));
            Assert.Equal((40f, 70f), scatter.LevelRange(2, 4, impostor: true));
            Assert.Equal((70f, 300f), scatter.LevelRange(3, 4, impostor: true));

            var material = Assert.IsType<ImpostorMaterial3D>(impostor.GetRenderMaterial(impostor.Multimesh!.Mesh!, 0));
            Assert.True(material.InstanceVisibility);
            Assert.Equal((70f, 300f, 2f), (material.InstanceVisibilityBegin, material.InstanceVisibilityEnd, material.InstanceVisibilityMargin));
            var lod1 = scatter.Batches.Single(b => b.Lod == 1);
            var leaves = Assert.IsType<FoliageMaterial3D>(lod1.GetRenderMaterial(lod1.Multimesh!.Mesh!, 1));
            Assert.Equal((true, 20f, 40f), (leaves.InstanceVisibility, leaves.InstanceVisibilityBegin, leaves.InstanceVisibilityEnd));
            Assert.Equal(AlphaMode.Cutout, ((FoliageMaterial3D)lod1.GetRenderMaterial(lod1.Multimesh!.Mesh!, 0)).RenderState.Alpha); // bark dithers its bands

            // A batch draws while one of its trees is in its level's range ± the margin (exactly, per tree).
            var lod0 = scatter.Batches.Single(b => b.Lod == 0);
            Assert.NotNull(lod1.InstanceOrigins);
            var box = lod1.CustomAabb!.Value;
            Assert.True(lod0.IsInVisibilityRange(new Vector3(0f, 0f, -5f), box));
            Assert.False(lod1.IsInVisibilityRange(new Vector3(0f, 0f, -5f), box)); // both trees nearer than 18 m
            Assert.True(lod1.IsInVisibilityRange(new Vector3(0f, 0f, -19f), box)); // the band: both levels
            Assert.True(lod0.IsInVisibilityRange(new Vector3(0f, 0f, -19f), box));
            Assert.False(lod0.IsInVisibilityRange(new Vector3(0f, 0f, -30f), box));
            Assert.True(impostor.IsInVisibilityRange(new Vector3(0f, 0f, -80f), box));
            Assert.False(impostor.IsInVisibilityRange(new Vector3(0f, 0f, -60f), box));

            // A prewarm clears the node's range: the batch draws (so its material and pipelines exist before play).
            var (begin, end) = (lod0.VisibilityRangeBegin, lod0.VisibilityRangeEnd);
            lod0.VisibilityRangeBegin = 0f;
            lod0.VisibilityRangeEnd = 0f;
            Assert.True(lod0.IsInVisibilityRange(new Vector3(0f, 0f, -30f), box));
            (lod0.VisibilityRangeBegin, lod0.VisibilityRangeEnd) = (begin, end);
            Assert.False(lod0.IsInVisibilityRange(new Vector3(0f, 0f, -30f), box));

            // ADR 0179: without a coarse shadow level every level casts where it is drawn.
            Assert.All(scatter.Batches, b => Assert.False(b.HasShadowRange));
            Assert.Equal((-1f, -1f), (material.InstanceShadowBegin, material.InstanceShadowEnd));

            // The impostor as the coarse shadow level takes over in the fine passes where level 1 (ShadowMaxLod) stops
            // casting, 40 m, not where it is drawn (70 m): the trees between them keep a shadow.
            scatter.ShadowCoarseLod = 3;
            Assert.Equal(40f, scatter.ShadowHandOff(4, impostor: true));
            Assert.Equal((40f, 0f), (material.InstanceShadowBegin, material.InstanceShadowEnd)); // from 40 m, unbounded
            Assert.Equal((-1f, -1f), (leaves.InstanceShadowBegin, leaves.InstanceShadowEnd));
            Assert.True(impostor.HasShadowRange);
            Assert.False(lod1.HasShadowRange);
            Assert.True(impostor.IsInShadowRange(new Vector3(0f, 0f, -45f), box));  // both trees ≥ 40 m away
            Assert.False(impostor.IsInShadowRange(new Vector3(0f, 0f, -30f), box)); // both nearer: level 0/1 casts
            Assert.True(impostor.IsInShadowRange(new Vector3(0f, 0f, -500f), box)); // past its visibility range too
            Assert.Equal(MeshRenderer.FinePasses | MeshRenderer.CoarsePasses, MeshRenderer.CasterPasses(impostor.ShadowCasterLod, true));

            // It follows ShadowMaxLod: level 2 casting hands off at the impostor's own begin, level 0 only at 20 m.
            scatter.ShadowMaxLod = 2;
            Assert.Equal(70f, material.InstanceShadowBegin);
            scatter.ShadowMaxLod = 0;
            Assert.Equal(20f, material.InstanceShadowBegin);
            Assert.Equal(20f, scatter.ShadowHandOff(4, impostor: true));

            scatter.ShadowCoarseLod = -1;
            Assert.Equal(-1f, material.InstanceShadowBegin);
            Assert.False(impostor.HasShadowRange);
        }
        finally
        {
            scatter.Free();
        }
    }
}
