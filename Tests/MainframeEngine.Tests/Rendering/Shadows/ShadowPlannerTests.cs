using System.Numerics;
using System.Runtime.CompilerServices;

namespace MainframeEngine.Tests.Rendering;

/// <summary>Which lights get which maps, the frame's passes and the shader uniforms (no GPU).</summary>
public sealed class ShadowPlannerTests
{
    private static PerspectiveCamera Camera(Vector3? position = null) => new()
    {
        Position = position ?? new Vector3(0, 3, 8),
        Forward = Vector3.Normalize(new Vector3(0, -0.3f, -1f)),
        AspectRatio = 16f / 9f,
        FieldOfView = 60f,
        Near = 0.1f,
        Far = 500f,
    };

    private static DirectionalLight Sun(bool shadows = true) =>
        new() { Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)), CastsShadows = shadows };

    private static SpotLight Spot(float x, int resolution = SpotLight.DefaultShadowResolution) => new()
    {
        Position = new Vector3(x, 5, 0),
        Direction = -Vector3.UnitY,
        Range = 12f,
        OuterConeAngle = 30f,
        ShadowResolution = resolution,
    };

    private static PointLight Point(float x) => new() { Position = new Vector3(x, 2, 0), Range = 8f };

    private static LightEnvironment Lights(params Light[] lights)
    {
        var environment = new LightEnvironment();
        foreach (var light in lights)
            environment.AddLight(light);
        return environment;
    }

    [Fact]
    public void ASunGetsFourCascadesFittedToTheCamera()
    {
        var planner = new ShadowPlanner();
        planner.Plan(Lights(Sun()), Camera(), Aabb.Empty);

        Assert.Equal(4, planner.PassCount);
        Assert.All(planner.Passes.ToArray(), p => Assert.Equal(ShadowPassKind.Cascade, p.Kind));
        Assert.Equal(4, planner.CascadeCount);
        Assert.Equal(DirectionalLight.DefaultShadowResolution, planner.CascadeResolution);
        Assert.Equal(1, planner.Uniforms.DirCodes[0]);
        Assert.Equal(new Vector4(4, DirectionalLight.DefaultCascadeBlend, DirectionalLight.DefaultMaxShadowDistance, 0), planner.Uniforms.Csm);

        var splits = planner.Uniforms.CascadeSplits;
        Assert.True(splits.X < splits.Y && splits.Y < splits.Z && splits.Z < splits.W);
        Assert.Equal(DirectionalLight.DefaultMaxShadowDistance, splits.W); // camera far (500) is further
        Assert.Equal(Vector4.One, planner.Uniforms.CascadeEnabled);
        for (var c = 0; c < 4; c++)
        {
            Assert.Equal(c, planner.Passes[c].Slot);
            Assert.Equal(planner.Passes[c].ViewProjection, planner.Uniforms.Cascades[c].ViewProjection);
            Assert.True(planner.Uniforms.Cascades[c].Params.X > 0f); // texel world size
            if (c > 0)
                Assert.True(planner.Uniforms.Cascades[c].Params.X > planner.Uniforms.Cascades[c - 1].Params.X, "later cascades cover more");
        }

        Assert.Equal(1f / 2048, planner.Uniforms.Filter.Z);
    }

    [Fact]
    public void TheShadowDistanceIsLimitedByTheCameraFarPlane()
    {
        var planner = new ShadowPlanner();
        var camera = Camera();
        camera.Far = 40f;
        planner.Plan(Lights(Sun()), camera, Aabb.Empty);
        Assert.Equal(40f, planner.Uniforms.CascadeSplits.W, 2);
    }

    [Fact]
    public void LightsWithoutShadowsGetNoMapsAndCodeZero()
    {
        var planner = new ShadowPlanner();
        var spot = Spot(0);
        spot.CastsShadows = false;
        var point = Point(1);
        point.CastsShadows = false;
        planner.Plan(Lights(Sun(shadows: false), spot, point), Camera(), Aabb.Empty);

        Assert.Equal(0, planner.PassCount);
        Assert.Equal(0, planner.Uniforms.DirCodes[0]);
        Assert.Equal(0, planner.Uniforms.SpotCodes[0]);
        Assert.Equal(0, planner.Uniforms.PointCodes[0]);
        Assert.Equal(0, planner.AtlasSize);
        Assert.Equal(0, planner.CascadeResolution);
    }

    [Fact]
    public void EveryLightTypeCastsAtOnce()
    {
        // Two suns, three spots, two points: 4 cascades + 4 atlas tiles + 12 cube faces.
        var planner = new ShadowPlanner();
        planner.Plan(Lights(Sun(), Sun(), Spot(-3), Spot(0), Spot(3), Point(-2), Point(2)), Camera(), Aabb.Empty);

        Assert.Equal(20, planner.PassCount);
        Assert.Equal(4, planner.Passes.ToArray().Count(p => p.Kind == ShadowPassKind.Cascade));
        Assert.Equal(4, planner.Passes.ToArray().Count(p => p.Kind == ShadowPassKind.AtlasTile));
        Assert.Equal(12, planner.Passes.ToArray().Count(p => p.Kind == ShadowPassKind.CubeFace));
        for (var i = 0; i < planner.PassCount; i++)
            Assert.Equal(i, planner.Passes[i].Index);

        Assert.Equal([1, 2, 0, 0], ToArray(planner.Uniforms.DirCodes)); // primary, then atlas map 0
        Assert.Equal([2, 3, 4, 0, 0, 0, 0, 0], ToArray(planner.Uniforms.SpotCodes)); // atlas maps 1..3
        Assert.Equal(1, planner.Uniforms.PointCodes[0]);
        Assert.Equal(2, planner.Uniforms.PointCodes[1]);

        // Secondary sun (2048 → a 1024 tile) + three 1024 spots → a 2048 atlas; tiles disjoint, rects match the tiles.
        Assert.Equal(2048, planner.AtlasSize);
        var tiles = Enumerable.Range(0, planner.AtlasMapCount).Select(planner.AtlasTile).ToArray();
        Assert.Equal([1024, 1024, 1024, 1024], tiles.Select(t => t.Size));
        for (var a = 0; a < tiles.Length; a++)
        {
            var rect = planner.Uniforms.AtlasMaps[a].Rect;
            Assert.Equal(new Vector4(tiles[a].X, tiles[a].Y, tiles[a].Size, tiles[a].Size) / 2048f, rect);
            for (var b = a + 1; b < tiles.Length; b++)
                Assert.False(tiles[a].Overlaps(tiles[b]));
        }

        // Spots are perspective maps whose texel size scales with distance; the secondary sun is orthographic.
        Assert.Equal(-DirectionalLight.DefaultMaxShadowDistance, planner.Uniforms.AtlasMaps[0].Params.Y); // ortho, up to its distance
        Assert.Equal(1f, planner.Uniforms.AtlasMaps[1].Params.Y);
        Assert.Equal(ShadowMath.SpotTexelPerDistance(30f, 1024), planner.Uniforms.AtlasMaps[1].Params.X, 6);

        var face = planner.Passes.ToArray().First(p => p.Kind == ShadowPassKind.CubeFace);
        Assert.Equal(new Vector3(-2, 2, 0), face.LightPosition);
        Assert.Equal(8f, face.LightRange);
        Assert.Equal(PointLight.DefaultShadowResolution, face.Size);
        Assert.Equal(2f / PointLight.DefaultShadowResolution, planner.Uniforms.PointParams[0].X);
    }

    [Fact]
    public void PerLightResolutionSizesTilesCubesAndTheAtlas()
    {
        var planner = new ShadowPlanner();
        var point = Point(0);
        point.ShadowResolution = 1000; // nearest power of two: 1024
        planner.Plan(Lights(Spot(-3, 512), Spot(0, 512), Spot(3, 512), point), Camera(), Aabb.Empty);

        Assert.Equal(1024, planner.AtlasSize); // three 512 tiles need a 1024 atlas, not the 4096 maximum
        Assert.All(Enumerable.Range(0, 3).Select(planner.AtlasTile), t => Assert.Equal(512, t.Size));
        Assert.Equal(1024, planner.CubeResolutions[0]);
    }

    [Fact]
    public void QualityLimitsCapCascadesAndEveryMapSize()
    {
        var planner = new ShadowPlanner { CascadeLimit = 2, ResolutionLimit = 1000 }; // rounded up to 1024
        Assert.Equal(1024, planner.ResolutionLimit);
        var point = Point(0);
        point.ShadowResolution = 2048;
        var secondSun = Sun();
        secondSun.ShadowResolution = 4096; // a 2048 tile, capped to 1024
        planner.Plan(Lights(Sun(), secondSun, Spot(-3, 2048), point), Camera(), Aabb.Empty);

        Assert.Equal(2, planner.CascadeCount); // the sun asks for 4
        Assert.Equal(2f, planner.Uniforms.Csm.X);
        Assert.Equal(1024, planner.CascadeResolution); // the sun asks for 2048
        Assert.Equal(1024, planner.CubeResolutions[0]);
        Assert.All(Enumerable.Range(0, planner.AtlasMapCount).Select(planner.AtlasTile), t => Assert.Equal(1024, t.Size));
        Assert.Equal(2 + 2 + 6, planner.PassCount);

        // Limits above a light's own settings change nothing; the cascade limit is clamped to 1..4.
        planner.CascadeLimit = 9;
        planner.ResolutionLimit = Light.MaxShadowResolution;
        planner.Plan(Lights(Sun()), Camera(), Aabb.Empty);
        Assert.Equal((4, DirectionalLight.DefaultShadowResolution), (planner.CascadeCount, planner.CascadeResolution));
        planner.CascadeLimit = 0;
        Assert.Equal(1, planner.CascadeLimit);
    }

    [Fact]
    public void TheAtlasIsRepackedOnlyWhenItsTilesChange()
    {
        var planner = new ShadowPlanner();
        var spot = Spot(0);
        var lights = Lights(spot, Spot(3));
        planner.Plan(lights, Camera(), Aabb.Empty);
        var tile = planner.AtlasTile(0);
        planner.Plan(lights, Camera(new Vector3(5, 3, 8)), Aabb.Empty);
        planner.Plan(lights, Camera(), Aabb.Empty);
        Assert.Equal(1, planner.AtlasPackCount);
        Assert.Equal(tile, planner.AtlasTile(0));

        spot.ShadowResolution = 2048;
        planner.Plan(lights, Camera(), Aabb.Empty);
        Assert.Equal(2, planner.AtlasPackCount);
        Assert.Equal(2048, planner.AtlasTile(0).Size);

        planner.MaxAtlasSize = 2048;
        planner.Plan(lights, Camera(), Aabb.Empty);
        Assert.Equal(3, planner.AtlasPackCount);
        Assert.Equal(2048, planner.AtlasSize);
        Assert.Equal(1024, planner.AtlasTile(0).Size); // limited to half the atlas
    }

    [Fact]
    public void OnlyTheFirstFourShadowedPointLightsGetCubes()
    {
        var planner = new ShadowPlanner();
        var lights = new Light[6];
        for (var i = 0; i < lights.Length; i++)
            lights[i] = Point(i);
        ((PointLight)lights[1]).CastsShadows = false;
        planner.Plan(Lights(lights), Camera(), Aabb.Empty);

        Assert.Equal([1, 0, 2, 3, 4, 0], ToArray(planner.Uniforms.PointCodes)[..6]);
        Assert.Equal(24, planner.PassCount);
    }

    [Fact]
    public void PassesWithoutCastersTurnTheirShadowsOff()
    {
        var planner = new ShadowPlanner();
        planner.Plan(Lights(Sun(), Spot(0), Point(0)), Camera(), Aabb.Empty);
        // Cascade 2 and the spot tile are empty; one cube face of the point light has a caster.
        foreach (var pass in planner.Passes)
        {
            var has = pass.Kind switch
            {
                ShadowPassKind.Cascade => pass.Slot != 2,
                ShadowPassKind.AtlasTile => false,
                _ => pass.Face == 3,
            };
            planner.SetHasCasters(pass.Index, has);
        }

        planner.ApplyCulling();
        Assert.Equal(new Vector4(1, 1, 0, 1), planner.Uniforms.CascadeEnabled);
        Assert.Equal(1, planner.Uniforms.DirCodes[0]);
        Assert.Equal(0, planner.Uniforms.SpotCodes[0]);
        Assert.Equal(1, planner.Uniforms.PointCodes[0]);
        Assert.True(planner.CubeRenders(0));
        Assert.All(planner.Passes.ToArray().Where(p => p.IsPoint), p => Assert.True(planner.HasCasters(p.Index))); // every face renders

        foreach (var pass in planner.Passes)
            planner.SetHasCasters(pass.Index, false);
        planner.ApplyCulling();
        Assert.Equal(0, planner.Uniforms.DirCodes[0]);
        Assert.Equal(0, planner.Uniforms.PointCodes[0]);
        Assert.False(planner.CubeRenders(0));
    }

    [Fact]
    public void CascadesStayPutWhileTheCameraMovesLessThanATexel()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun());
        var camera = Camera();
        planner.Plan(lights, camera, Aabb.Empty);
        var before = planner.Passes.ToArray();

        // A world point keeps its position within its texel in every cascade (whole-texel shifts at most).
        var point = new Vector3(0.37f, 0f, -1.21f);
        for (var step = 1; step <= 10; step++)
        {
            camera.Position = new Vector3(0, 3, 8) + new Vector3(0.0003f, 0.0001f, -0.0002f) * step;
            planner.Plan(lights, camera, Aabb.Empty);
            for (var c = 0; c < 4; c++)
            {
                var resolution = planner.CascadeResolution;
                var delta = Texel(planner.Passes[c].ViewProjection, point, resolution) - Texel(before[c].ViewProjection, point, resolution);
                Assert.Equal(MathF.Round(delta.X), delta.X, 2);
                Assert.Equal(MathF.Round(delta.Y), delta.Y, 2);
            }
        }
    }

    private static Vector2 Texel(in Matrix4x4 matrix, Vector3 world, int resolution)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), matrix);
        return (new Vector2(clip.X, clip.Y) / clip.W * 0.5f + new Vector2(0.5f)) * resolution;
    }

    [Fact]
    public void CastersInFrontOfACascadePullItsNearPlaneBack()
    {
        var planner = new ShadowPlanner();
        var sun = new DirectionalLight { Direction = -Vector3.UnitY };
        var tower = new Aabb(new Vector3(-1, 0, -1), new Vector3(1, 300, 1));
        planner.Plan(Lights(sun), Camera(), tower);

        // The tower top (y = 300) is in front of every cascade's near plane: depth ≥ 0 there.
        for (var c = 0; c < planner.CascadeCount; c++)
        {
            var top = Vector4.Transform(new Vector4(0, 299, 0, 1), planner.Passes[c].ViewProjection);
            Assert.InRange(top.Z, 0f, 1f);
        }
    }

    [Fact]
    public void WithoutACameraTheSunCoversAFixedSphere()
    {
        var planner = new ShadowPlanner();
        planner.Plan(Lights(Sun()), null, Aabb.Empty);
        Assert.Equal(1, planner.CascadeCount);
        var origin = Vector4.Transform(new Vector4(0, 0, 0, 1), planner.Passes[0].ViewProjection);
        Assert.Equal(0f, origin.X, 1);
        Assert.Equal(0f, origin.Y, 1);
    }

    [Fact]
    public void SettingsReachTheUniforms()
    {
        var planner = new ShadowPlanner { Filter = ShadowFilter.Pcf3x3, FilterRadius = 2.5f, DebugCascades = true };
        var sun = Sun();
        sun.CascadeCount = 2;
        sun.ShadowResolution = 1024;
        sun.ShadowBias = 0.25f;
        sun.ShadowNormalBias = 3f;
        planner.Plan(Lights(sun), Camera(), Aabb.Empty);

        Assert.Equal(2, planner.CascadeCount);
        Assert.Equal(new Vector4((float)ShadowFilter.Pcf3x3, 2.5f, 1f / 1024, 0f), planner.Uniforms.Filter);
        Assert.Equal(1f, planner.Uniforms.Csm.W);
        Assert.Equal(0.25f, planner.Uniforms.Cascades[0].Params.Z);
        Assert.Equal(3f, planner.Uniforms.Cascades[0].Params.W);
    }

    [Fact]
    public void PlanningAllocatesNothing()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun(), Sun(), Spot(-3), Spot(0), Spot(3), Point(-2), Point(2));
        var camera = Camera();
        var bounds = new Aabb(new Vector3(-10, 0, -10), new Vector3(10, 4, 10));
        planner.Plan(lights, camera, bounds);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            camera.Position += new Vector3(0.01f, 0, 0);
            planner.Plan(lights, camera, bounds);
            foreach (var pass in planner.Passes)
                planner.SetHasCasters(pass.Index, pass.Index % 3 != 0);
            planner.ApplyCulling();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void UniformLayoutMatchesTheShaderBlock()
    {
        // std140 offsets of ShadowUBO in include/shadows.glsl (spirv-reflect of Mesh.vk.frag).
        Assert.Equal(96, Unsafe.SizeOf<ShadowMapData>());
        Assert.Equal(1680, ShadowUniforms.Size);
        var u = default(ShadowUniforms);
        Assert.Equal(0, Offset(ref u, ref Unsafe.As<ShadowCascadeArray, byte>(ref u.Cascades)));
        Assert.Equal(384, Offset(ref u, ref Unsafe.As<ShadowAtlasMapArray, byte>(ref u.AtlasMaps)));
        Assert.Equal(1440, Offset(ref u, ref Unsafe.As<Vector4, byte>(ref u.CascadeSplits)));
        Assert.Equal(1456, Offset(ref u, ref Unsafe.As<Vector4, byte>(ref u.CascadeEnabled)));
        Assert.Equal(1472, Offset(ref u, ref Unsafe.As<Vector4, byte>(ref u.Csm)));
        Assert.Equal(1488, Offset(ref u, ref Unsafe.As<Vector4, byte>(ref u.Filter)));
        Assert.Equal(1504, Offset(ref u, ref Unsafe.As<ShadowDirCodes, byte>(ref u.DirCodes)));
        Assert.Equal(1520, Offset(ref u, ref Unsafe.As<ShadowSpotCodes, byte>(ref u.SpotCodes)));
        Assert.Equal(1552, Offset(ref u, ref Unsafe.As<ShadowPointCodes, byte>(ref u.PointCodes)));
        Assert.Equal(1616, Offset(ref u, ref Unsafe.As<ShadowPointParams, byte>(ref u.PointParams)));
    }

    private static int Offset(ref ShadowUniforms u, ref byte field) =>
        (int)Unsafe.ByteOffset(ref Unsafe.As<ShadowUniforms, byte>(ref u), ref field);

    private static int[] ToArray(ShadowDirCodes codes) => ((ReadOnlySpan<int>)codes).ToArray();

    private static int[] ToArray(ShadowSpotCodes codes) => ((ReadOnlySpan<int>)codes).ToArray();

    private static int[] ToArray(ShadowPointCodes codes) => ((ReadOnlySpan<int>)codes).ToArray();
}
