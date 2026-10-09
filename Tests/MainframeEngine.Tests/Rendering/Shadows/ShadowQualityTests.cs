using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// G8e.2 shadow quality (ADR 0167) on the CPU: the staggered cascade schedule and cache, coarse passes, the far shadow,
/// the PCSS and contact-shadow uniforms and the penumbra math (no GPU).
/// </summary>
public sealed class ShadowQualityTests
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

    private static DirectionalLight Sun(ShadowCacheMode cache = ShadowCacheMode.Staggered) => new()
    {
        Direction = Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)),
        CacheMode = cache,
        MaxShadowDistance = 140f,
    };

    private static LightEnvironment Lights(params Light[] lights)
    {
        var environment = new LightEnvironment();
        foreach (var light in lights)
            environment.AddLight(light);
        return environment;
    }

    private static readonly Aabb Valley = new(new Vector3(-128, -5, -128), new Vector3(128, 40, 128));

    // Plans a frame and reports every pass as having casters.
    private static void Frame(ShadowPlanner planner, LightEnvironment lights, ICamera camera, in Aabb bounds)
    {
        planner.Plan(lights, camera, bounds);
        foreach (var pass in planner.Passes)
            planner.SetHasCasters(pass.Index, true);
        planner.ApplyCulling();
    }

    private static int CascadePasses(ShadowPlanner planner)
    {
        var count = 0;
        foreach (var pass in planner.Passes)
            if (pass.Kind == ShadowPassKind.Cascade)
                count++;
        return count;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void TheScheduleNeverRendersMoreThanTwoCascadesAFrame(int count)
    {
        var last = new long[count];
        Array.Fill(last, -1);
        for (ulong frame = 0; frame < 64; frame++)
        {
            var due = 0;
            for (var c = 0; c < count; c++)
            {
                if (!ShadowCacheSchedule.IsDue(c, count, frame))
                    continue;
                due++;
                if (last[c] >= 0)
                    Assert.Equal(ShadowCacheSchedule.Interval(c, count), (int)((long)frame - last[c]));
                last[c] = (long)frame;
            }

            Assert.True(due <= 2, $"{due} cascades due on frame {frame} of {count}");
            Assert.True(ShadowCacheSchedule.IsDue(0, count, frame), "cascade 0 renders every frame");
        }

        // 60 / 30 / 15 / 15 Hz at 60 fps with four cascades.
        if (count == 4)
            Assert.Equal([1, 2, 4, 4], Enumerable.Range(0, 4).Select(c => ShadowCacheSchedule.Interval(c, 4)).ToArray());
    }

    [Fact]
    public void TheMarginCoversTheCameraTravelOverTheInterval()
    {
        Assert.Equal(0f, ShadowCacheSchedule.Margin(0, 4, 10f));
        Assert.Equal(10f * 2 / 60f, ShadowCacheSchedule.Margin(1, 4, 10f), 5);
        Assert.Equal(10f * 4 / 60f, ShadowCacheSchedule.Margin(3, 4, 10f), 5); // 0.67 m: the proposal's 0.7 m
        Assert.Equal(0f, ShadowCacheSchedule.Margin(2, 4, -3f));

        // Turning at 60°/s for 4 frames (4°) swings a sphere 80 m ahead by 2 · 80 · sin 2° = 5.6 m.
        Assert.Equal(10f * 4 / 60f + 160f * MathF.Sin(float.DegreesToRadians(2f)), ShadowCacheSchedule.Margin(3, 4, 10f, 60f, 80f), 4);
        Assert.Equal(0f, ShadowCacheSchedule.Margin(0, 4, 10f, 60f, 80f));
    }

    [Fact]
    public void ACameraTurningWithinTheTurnRateKeepsTheSchedule()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun());
        var camera = Camera();
        Frame(planner, lights, camera, Valley);
        var yaw = 0f;
        for (var f = 0; f < 32; f++)
        {
            yaw += float.DegreesToRadians(0.9f); // 54°/s at 60 Hz, under the default 60°/s
            camera.Forward = Vector3.Normalize(new Vector3(MathF.Sin(yaw), -0.3f, -MathF.Cos(yaw)));
            Frame(planner, lights, camera, Valley);
            Assert.True(planner.RenderedCascadeCount <= 2, $"frame {f}: {planner.RenderedCascadeCount} cascades rendered while turning");
        }
    }

    [Fact]
    public void AStaggeredSunRendersAllCascadesOnceThenAtMostTwoAFrame()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun());
        var camera = Camera();
        Frame(planner, lights, camera, Valley);
        Assert.Equal(4, planner.RenderedCascadeCount);
        Assert.Equal(0, planner.CachedCascadeCount);

        var first = planner.Uniforms;
        for (var f = 0; f < 16; f++)
        {
            Frame(planner, lights, camera, Valley);
            Assert.True(planner.RenderedCascadeCount <= 2, $"{planner.RenderedCascadeCount} cascades rendered");
            Assert.Equal(4, planner.RenderedCascadeCount + planner.CachedCascadeCount);
            Assert.Equal(planner.RenderedCascadeCount, CascadePasses(planner));
            Assert.True(planner.Passes.ToArray().Any(p => p.Kind == ShadowPassKind.Cascade && p.Slot == 0), "cascade 0 renders every frame");

            // A still camera: every cascade keeps its matrix, cached or re-rendered.
            for (var c = 0; c < 4; c++)
            {
                Assert.Equal(first.Cascades[c].ViewProjection, planner.Uniforms.Cascades[c].ViewProjection);
                Assert.Equal(1f, planner.Uniforms.CascadeEnabled[c]);
                Assert.Equal(first.CascadeDepthRange[c], planner.Uniforms.CascadeDepthRange[c]);
            }
        }
    }

    [Fact]
    public void WithoutACacheEveryCascadeRendersEveryFrameAsBefore()
    {
        var off = new ShadowPlanner();
        var staggered = new ShadowPlanner();
        var camera = Camera();
        var offLights = Lights(Sun(ShadowCacheMode.Off));
        for (var f = 0; f < 6; f++)
        {
            Frame(off, offLights, camera, Valley);
            Assert.Equal(4, off.RenderedCascadeCount);
            Assert.Equal(0, off.CachedCascadeCount);
        }

        // Cascade 0 is never grown; the cached ones are grown by the camera's travel (a slightly coarser texel).
        Frame(staggered, Lights(Sun()), camera, Valley);
        Assert.Equal(off.Uniforms.Cascades[0].ViewProjection, staggered.Uniforms.Cascades[0].ViewProjection);
        for (var c = 1; c < 4; c++)
            Assert.True(staggered.Uniforms.Cascades[c].Params.X > off.Uniforms.Cascades[c].Params.X);
    }

    [Fact]
    public void ACascadeTheCameraLeavesRendersEarly()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun());
        var camera = Camera();
        Frame(planner, lights, camera, Valley);
        Frame(planner, lights, camera, Valley);

        // Within the margin (8 m/s for one 60 Hz frame): the schedule decides.
        camera.Position += new Vector3(8f / 60f, 0, 0);
        Frame(planner, lights, camera, Valley);
        Assert.True(planner.RenderedCascadeCount <= 2);

        // A teleport: every cascade's slice leaves its sphere.
        camera.Position += new Vector3(50f, 0, 0);
        Frame(planner, lights, camera, Valley);
        Assert.Equal(4, planner.RenderedCascadeCount);
    }

    [Fact]
    public void TurningTheLightOrInvalidatingReRendersEveryCascade()
    {
        var planner = new ShadowPlanner();
        var sun = Sun();
        var lights = Lights(sun);
        var camera = Camera();
        Frame(planner, lights, camera, Valley);
        Frame(planner, lights, camera, Valley);
        Assert.True(planner.CachedCascadeCount > 0);

        sun.Direction = Vector3.Normalize(sun.Direction + new Vector3(0.01f, 0, 0));
        Frame(planner, lights, camera, Valley);
        Assert.Equal(4, planner.RenderedCascadeCount);

        Frame(planner, lights, camera, Valley);
        Assert.True(planner.CachedCascadeCount > 0);
        planner.InvalidateCache();
        Frame(planner, lights, camera, Valley);
        Assert.Equal(4, planner.RenderedCascadeCount);

        sun.ShadowResolution = 1024;
        Frame(planner, lights, camera, Valley);
        Assert.Equal(4, planner.RenderedCascadeCount);
    }

    [Fact]
    public void ACascadeWithoutCastersStaysDisabledWhileCached()
    {
        var planner = new ShadowPlanner();
        var lights = Lights(Sun());
        var camera = Camera();
        planner.Plan(lights, camera, Valley);
        foreach (var pass in planner.Passes)
            planner.SetHasCasters(pass.Index, pass.Slot != 3);
        planner.ApplyCulling();
        Assert.Equal(0f, planner.Uniforms.CascadeEnabled[3]);

        planner.Plan(lights, camera, Valley); // frame 2: cascade 3 is cached
        Assert.DoesNotContain(planner.Passes.ToArray(), p => p.Kind == ShadowPassKind.Cascade && p.Slot == 3);
        Assert.Equal(0f, planner.Uniforms.CascadeEnabled[3]);
        Assert.Equal(1f, planner.Uniforms.CascadeEnabled[2]);
    }

    [Fact]
    public void TheLastCascadesAreCoarsePasses()
    {
        var planner = new ShadowPlanner();
        var sun = Sun(ShadowCacheMode.Off);
        sun.CoarseCascades = 2;
        planner.Plan(Lights(sun), Camera(), Valley);
        Assert.Equal([false, false, true, true], planner.Passes.ToArray().Select(p => p.Coarse).ToArray());

        sun.CoarseCascades = 0;
        planner.Plan(Lights(sun), Camera(), Valley);
        Assert.All(planner.Passes.ToArray(), p => Assert.False(p.Coarse));
    }

    [Fact]
    public void CastersGoIntoThePassesTheirLevelAndRangeAllow()
    {
        // ADR 0167 / 0179: in range (visibility or shadow range) every caster but a coarse-only one casts into the fine
        // passes; a coarse caster casts into the coarse passes from any distance; a fine one never does.
        const byte fine = MeshRenderer.FinePasses, coarse = MeshRenderer.CoarsePasses, both = fine | coarse;
        Assert.Equal(both, MeshRenderer.CasterPasses(ShadowCasterLod.All, castsFine: true));
        Assert.Equal(0, MeshRenderer.CasterPasses(ShadowCasterLod.All, castsFine: false));
        Assert.Equal(fine, MeshRenderer.CasterPasses(ShadowCasterLod.Fine, castsFine: true));
        Assert.Equal(0, MeshRenderer.CasterPasses(ShadowCasterLod.Fine, castsFine: false));
        Assert.Equal(both, MeshRenderer.CasterPasses(ShadowCasterLod.Coarse, castsFine: true));
        Assert.Equal(coarse, MeshRenderer.CasterPasses(ShadowCasterLod.Coarse, castsFine: false));

        // A node without a shadow range casts where it is drawn.
        var node = new MeshInstance3D { VisibilityRangeBegin = 10f, VisibilityRangeEnd = 20f };
        var bounds = new Aabb(new Vector3(-1f), new Vector3(1f));
        Assert.False(node.HasShadowRange);
        Assert.Equal(node.IsInVisibilityRange(new Vector3(0f, 0f, 15f), bounds), node.IsInShadowRange(new Vector3(0f, 0f, 15f), bounds));
        Assert.Equal(node.IsInVisibilityRange(new Vector3(0f, 0f, 25f), bounds), node.IsInShadowRange(new Vector3(0f, 0f, 25f), bounds));
    }

    [Fact]
    public void TheFarShadowRendersOnceAndAgainOnlyWhenTheSunTurnsOrTheBoundsGrow()
    {
        var planner = new ShadowPlanner();
        var sun = Sun(ShadowCacheMode.Off);
        sun.FarShadowEnabled = true;
        var lights = Lights(sun);
        var camera = Camera();

        Frame(planner, lights, camera, Valley);
        Assert.True(planner.FarShadowRenders);
        Assert.Equal(ShadowPlanner.FarShadowLayer + 1, planner.CascadeLayers);
        var far = planner.Passes.ToArray().Single(p => p.Kind == ShadowPassKind.FarShadow);
        Assert.True(far.Coarse);
        Assert.Equal(ShadowPlanner.FarShadowLayer, far.Slot);
        Assert.Equal(1f, planner.Uniforms.FarParams.X);
        Assert.Equal(far.ViewProjection, planner.Uniforms.FarMap.ViewProjection);

        // The whole valley lies inside the far map, depth in [0, 1].
        foreach (var corner in Corners(Valley))
        {
            var clip = Vector4.Transform(new Vector4(corner, 1f), far.ViewProjection);
            Assert.InRange(clip.X / clip.W, -1f, 1f);
            Assert.InRange(clip.Y / clip.W, -1f, 1f);
            Assert.InRange(clip.Z / clip.W, 0f, 1f);
        }

        // A moving camera and a sun turning by less than 0.1° reuse it.
        camera.Position += new Vector3(30, 0, 0);
        sun.Direction = Vector3.Normalize(sun.Direction + new Vector3(0.0005f, 0, 0));
        Frame(planner, lights, camera, Valley);
        Assert.False(planner.FarShadowRenders);
        Assert.Equal(1f, planner.Uniforms.FarParams.X);
        Assert.Equal(far.ViewProjection, planner.Uniforms.FarMap.ViewProjection);

        sun.Direction = Vector3.Normalize(sun.Direction + new Vector3(0.01f, 0, 0));
        Frame(planner, lights, camera, Valley);
        Assert.True(planner.FarShadowRenders);

        Frame(planner, lights, camera, new Aabb(Valley.Min, Valley.Max + new Vector3(20, 0, 0)));
        Assert.True(planner.FarShadowRenders);
        Frame(planner, lights, camera, Valley);
        Assert.False(planner.FarShadowRenders); // smaller bounds stay covered

        sun.FarShadowEnabled = false;
        Frame(planner, lights, camera, Valley);
        Assert.Equal(Vector4.Zero, planner.Uniforms.FarParams);
        Assert.Equal(ShaderLimits.MaxShadowCascades, planner.CascadeLayers);
    }

    [Fact]
    public void AFarShadowWithoutCastersIsOffAndNotReRendered()
    {
        var planner = new ShadowPlanner();
        var sun = Sun(ShadowCacheMode.Off);
        sun.FarShadowEnabled = true;
        var lights = Lights(sun);
        planner.Plan(lights, Camera(), Valley);
        foreach (var pass in planner.Passes)
            planner.SetHasCasters(pass.Index, pass.Kind != ShadowPassKind.FarShadow);
        planner.ApplyCulling();
        Assert.Equal(Vector4.Zero, planner.Uniforms.FarParams);

        Frame(planner, lights, Camera(), Valley);
        Assert.False(planner.FarShadowRenders);
        Assert.Equal(Vector4.Zero, planner.Uniforms.FarParams);
    }

    [Fact]
    public void AFarShadowDistanceCoversABoxAroundTheCamera()
    {
        var planner = new ShadowPlanner();
        var sun = Sun(ShadowCacheMode.Off);
        sun.FarShadowEnabled = true;
        sun.FarShadowDistance = 50f;
        var lights = Lights(sun);
        var camera = Camera(new Vector3(0, 3, 0));
        Frame(planner, lights, camera, Valley);
        Assert.True(planner.FarShadowRenders);
        var texel = planner.Uniforms.FarMap.Params.X;

        // A move within a quarter of the distance keeps it; a longer one re-centres it.
        camera.Position += new Vector3(5, 0, 0);
        Frame(planner, lights, camera, Valley);
        Assert.False(planner.FarShadowRenders);
        camera.Position += new Vector3(30, 0, 0);
        Frame(planner, lights, camera, Valley);
        Assert.True(planner.FarShadowRenders);
        Assert.Equal(texel, planner.Uniforms.FarMap.Params.X, 2);
    }

    private static IEnumerable<Vector3> Corners(Aabb box)
    {
        for (var i = 0; i < 8; i++)
            yield return new Vector3((i & 1) == 0 ? box.Min.X : box.Max.X, (i & 2) == 0 ? box.Min.Y : box.Max.Y, (i & 4) == 0 ? box.Min.Z : box.Max.Z);
    }

    [Fact]
    public void PcssAndContactShadowUniformsFollowTheLightAndTheQuality()
    {
        var planner = new ShadowPlanner();
        var sun = Sun(ShadowCacheMode.Off);
        planner.Plan(Lights(sun), Camera(), Valley);
        Assert.Equal(ShadowFilter.Pcss, planner.Filter);
        Assert.Equal(Vector4.Zero, planner.Uniforms.Pcss); // no angular size: the fixed Poisson filter
        Assert.Equal(Vector4.Zero, planner.Uniforms.Contact);

        sun.AngularDistance = 0.5f;
        sun.ContactShadows = true;
        planner.Plan(Lights(sun), Camera(), Valley);
        Assert.Equal(MathF.Tan(float.DegreesToRadians(0.25f)), planner.Uniforms.Pcss.X, 6);
        Assert.Equal(ShadowPlanner.MaxPenumbraTexels, planner.Uniforms.Pcss.Y);
        Assert.Equal(1f, planner.Uniforms.Contact.X);
        Assert.Equal((float)ShadowFilter.Pcss, planner.Uniforms.Filter.X);
        for (var c = 0; c < 4; c++)
        {
            // An orthographic window of half-size h spans 2h + the pull-back in depth.
            var texel = planner.Uniforms.Cascades[c].Params.X;
            Assert.True(planner.Uniforms.CascadeDepthRange[c] >= texel * DirectionalLight.DefaultShadowResolution);
        }

        // Medium: no PCSS, no contact shadows.
        var medium = ShadowQualitySettings.For(ShadowQuality.Medium);
        planner.Filter = medium.Filter;
        planner.ContactShadows = medium.ContactShadows;
        planner.Plan(Lights(sun), Camera(), Valley);
        Assert.Equal(Vector4.Zero, planner.Uniforms.Pcss);
        Assert.Equal(Vector4.Zero, planner.Uniforms.Contact);
    }

    [Fact]
    public void ThePenumbraGrowsWithTheBlockerDistance()
    {
        // A 0.5° sun, a 2 cm texel: a blocker 1 m above the receiver gives a 0.4-texel penumbra (clamped up to the filter
        // radius), 20 m above an 8.7 cm one (4.4 texels), 100 m above more than the largest radius.
        var tan = MathF.Tan(float.DegreesToRadians(0.25f));
        const float range = 200f, texel = 0.02f;
        float Penumbra(float metres) => ShadowMath.PcssPenumbraTexels(0.5f, 0.5f - metres / range, range, tan, texel, 1.5f, 8f);
        Assert.Equal(1.5f, Penumbra(1f));
        Assert.Equal(20f * tan / texel, Penumbra(20f), 3);
        Assert.Equal(8f, Penumbra(100f));
        Assert.True(Penumbra(5f) < Penumbra(10f) && Penumbra(10f) < Penumbra(15f));
        Assert.Equal(1.5f, ShadowMath.PcssPenumbraTexels(0.5f, 0.6f, range, tan, texel, 1.5f, 8f)); // no blocker in front
    }

    [Fact]
    public void ContactShadowSettingsNeedTheLightAndTheQuality()
    {
        var sun = new DirectionalLight { ContactShadows = true, ContactShadowLength = 0.4f };
        var on = ContactShadowSettings.For(sun, allowed: true);
        Assert.True(on.Enabled);
        Assert.Equal(0.4f, on.Length);
        Assert.Equal(-Vector3.Normalize(sun.Direction), on.TowardsLight);
        Assert.False(ContactShadowSettings.For(sun, allowed: false).Enabled);
        Assert.False(ContactShadowSettings.For(null, allowed: true).Enabled);
        sun.CastsShadows = false;
        Assert.False(ContactShadowSettings.For(sun, allowed: true).Enabled);
        Assert.False(ContactShadowSettings.For(new DirectionalLight(), allowed: true).Enabled);
    }

    [Fact]
    public void TheContactShadowEffectRunsAfterThePrepassWhenALightAsks()
    {
        using var effect = new ContactShadows();
        Assert.Equal(PostStage.AfterPrepass, effect.Stage);
        Assert.Equal(PostEffectOrder.ContactShadows, effect.Order);
        Assert.Equal(PostEffectNeeds.DepthPrepass, effect.Needs);
        Assert.False(effect.IsEnabled(PostEffectSettings.Default));
        var settings = PostEffectSettings.Default with { ContactShadows = new ContactShadowSettings(true, Vector3.UnitY, 0.5f) };
        Assert.True(effect.IsEnabled(settings));
    }

    [Fact]
    public void StaggeredAndFarShadowPlanningAllocatesNothing()
    {
        var planner = new ShadowPlanner();
        var sun = Sun();
        sun.FarShadowEnabled = true;
        sun.CoarseCascades = 2;
        sun.AngularDistance = 0.5f;
        sun.ContactShadows = true;
        var lights = Lights(sun);
        var camera = Camera();
        Frame(planner, lights, camera, Valley);

        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                camera.Position += new Vector3(0.1f, 0, 0);
                Frame(planner, lights, camera, Valley);
            }
        }));
    }

    [Fact]
    public void NewDirectionalLightSettingsAreClampedAndOffByDefault()
    {
        var sun = new DirectionalLight();
        Assert.Equal(ShadowCacheMode.Off, sun.CacheMode);
        Assert.Equal(0f, sun.AngularDistance);
        Assert.False(sun.ContactShadows);
        Assert.Equal(DirectionalLight.DefaultContactShadowLength, sun.ContactShadowLength);
        Assert.Equal(0, sun.CoarseCascades);
        Assert.False(sun.FarShadowEnabled);
        Assert.Equal(0f, sun.FarShadowDistance);

        sun.AngularDistance = 200f;
        sun.ContactShadowLength = -1f;
        sun.CoarseCascades = 9;
        sun.FarShadowDistance = -5f;
        Assert.Equal(DirectionalLight.MaxAngularDistance, sun.AngularDistance);
        Assert.Equal(0.01f, sun.ContactShadowLength);
        Assert.Equal(3, sun.CoarseCascades);
        Assert.Equal(0f, sun.FarShadowDistance);
    }
}
