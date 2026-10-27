using System.Numerics;
using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Lighting;

/// <summary>
/// ADR 0217: over the UBO limits, <see cref="LightEnvironment.SelectLights"/> keeps the point and spot lights nearest the
/// camera (with hysteresis) at the front of their lists, which the lights UBO and the shadow planner both read.
/// </summary>
public sealed class LightSelectionTests
{
    private const int HeaderFloats = 12;
    private const int PointFloats = 8;

    // Point lights along +X every 10 units, range 2: the camera at x sits inside none of them unless it is within 2.
    private static (LightEnvironment Env, PointLight[] Added) Points(int count, float range = 2f)
    {
        var env = new LightEnvironment();
        var added = new PointLight[count];
        for (var n = 0; n < count; n++)
        {
            added[n] = new PointLight { Position = new Vector3(n * 10f, 0, 0), Range = range };
            env.AddLight(added[n]);
        }

        return (env, added);
    }

    private static Vector3[] UboPointPositions(LightEnvironment env, Vector3 camera)
    {
        var bytes = new byte[LightEnvironment.UboSize];
        env.WriteUbo(bytes, camera);
        var f = MemoryMarshal.Cast<byte, float>(bytes);
        var i = MemoryMarshal.Cast<byte, int>(bytes);
        var positions = new Vector3[i[9]];
        var o = HeaderFloats + LightEnvironment.MaxDirectional * 8;
        for (var n = 0; n < positions.Length; n++, o += PointFloats)
            positions[n] = new Vector3(f[o], f[o + 1], f[o + 2]);
        return positions;
    }

    [Fact]
    public void WithinTheLimitsNothingMoves()
    {
        var (env, added) = Points(LightEnvironment.MaxPoint);
        var spots = new SpotLight[LightEnvironment.MaxSpot];
        for (var n = 0; n < spots.Length; n++)
            env.AddLight(spots[n] = new SpotLight { Position = new Vector3(n * 10f, 5, 0) });

        env.SelectLights(new Vector3(1000, 0, 0)); // nearest the last-added lights

        Assert.Equal(added, env.PointLights);
        Assert.Equal(spots, env.SpotLights);
    }

    [Fact]
    public void OverTheLimitTheNearestLightsAreKept()
    {
        var (env, added) = Points(24);
        var camera = new Vector3(230, 0, 0); // at the last light: lights 8–23 are the nearest 16

        env.SelectLights(camera);

        // The selection keeps the order the lights were added in; the dropped ones follow.
        Assert.Equal(added[8..24], env.PointLights.Take(LightEnvironment.MaxPoint));
        Assert.Equal(added[8..24].Select(l => l.Position), UboPointPositions(env, camera));
        Assert.Equal(24, env.PointLights.Count);
        Assert.Equal(added.ToHashSet(), env.PointLights.ToHashSet());
    }

    [Fact]
    public void TheSelectionFollowsTheCamera()
    {
        var (env, added) = Points(24);

        env.SelectLights(new Vector3(230, 0, 0));
        env.SelectLights(new Vector3(0, 0, 0)); // back at the first light: 0–15

        Assert.Equal(added[..16], env.PointLights.Take(LightEnvironment.MaxPoint));
    }

    [Fact]
    public void LightsWhoseRangeContainsTheCameraRankFirst()
    {
        var env = new LightEnvironment();
        var near = new PointLight[LightEnvironment.MaxPoint];
        for (var n = 0; n < near.Length; n++)
            env.AddLight(near[n] = new PointLight { Position = new Vector3(5, 0, 0), Range = 1f }); // 4 units out of range
        var wide = new PointLight { Position = new Vector3(-50, 0, 0), Range = 60f };                  // far, but reaches the camera
        env.AddLight(wide);

        env.SelectLights(Vector3.Zero);

        var selected = env.PointLights.Take(LightEnvironment.MaxPoint).ToList();
        Assert.Contains(wide, selected);
        Assert.DoesNotContain(near[^1], selected); // the near lights tie: the one added last gives way
    }

    [Fact]
    public void TiesGoToTheLightAddedFirst()
    {
        var env = new LightEnvironment();
        var far = new SpotLight[LightEnvironment.MaxSpot];
        for (var n = 0; n < far.Length; n++)
            env.AddLight(far[n] = new SpotLight { Position = new Vector3(0, 100, 0), Range = 5f }); // all the same distance
        var near = new SpotLight[LightEnvironment.MaxSpot - 2];
        for (var n = 0; n < near.Length; n++)
            env.AddLight(near[n] = new SpotLight { Position = new Vector3(0, 30, 0), Range = 5f });

        env.SelectLights(Vector3.Zero);

        // Every near light, then the two far lights added first fill the last slots.
        Assert.Equal([far[0], far[1], .. near], env.SpotLights.Take(LightEnvironment.MaxSpot));
    }

    [Fact]
    public void ASelectedLightKeepsItsSlotUntilAChallengerIsClearlyNearer()
    {
        var env = new LightEnvironment();
        var ring = new PointLight[LightEnvironment.MaxPoint];
        for (var n = 0; n < ring.Length; n++)
            env.AddLight(ring[n] = new PointLight { Position = new Vector3(10, 0, 0), Range = 0f }); // all exactly 10 away

        var challenger = new PointLight { Position = new Vector3(0, 9.5f, 0), Range = 0f };
        env.AddLight(challenger);

        // 9.5 against 10: nearer, but within the margin (10 − 0.25 − 10% = 8.75).
        env.SelectLights(Vector3.Zero);
        Assert.Equal(ring, env.PointLights.Take(LightEnvironment.MaxPoint));

        // 8.5 beats it: the challenger takes the slot of the ring light added last.
        challenger.Position = new Vector3(0, 8.5f, 0);
        env.SelectLights(Vector3.Zero);
        var selected = env.PointLights.Take(LightEnvironment.MaxPoint).ToList();
        Assert.Contains(challenger, selected);
        Assert.Equal(ring[^1], env.PointLights[^1]);

        // The evicted light (10) does not win its slot back from the newcomer (8.5) either: no flicker at the cutoff.
        challenger.Position = new Vector3(0, 9.5f, 0);
        env.SelectLights(Vector3.Zero);
        Assert.Contains(challenger, env.PointLights.Take(LightEnvironment.MaxPoint));
        Assert.Equal(ring[^1], env.PointLights[^1]);
    }

    [Fact]
    public void BackUnderTheLimitTheListReturnsToTheOrderAdded()
    {
        var (env, added) = Points(LightEnvironment.MaxPoint + 1);
        env.SelectLights(new Vector3(160, 0, 0)); // drops light 0
        Assert.NotEqual(added[0], env.PointLights[0]);

        env.RemoveLight(added[5]);
        env.SelectLights(new Vector3(160, 0, 0));

        Assert.Equal(added.Where(l => l != added[5]), env.PointLights);
    }

    [Fact]
    public void DirectionalLightsAreNeverReordered()
    {
        var env = new LightEnvironment();
        var suns = new DirectionalLight[LightEnvironment.MaxDirectional + 2];
        for (var n = 0; n < suns.Length; n++)
            env.AddLight(suns[n] = new DirectionalLight { Position = new Vector3(n * 100f, 0, 0) });

        env.SelectLights(new Vector3(1000, 0, 0));

        Assert.Equal(suns, env.DirectionalLights);
    }

    [Fact]
    public void TheShadowPlannerShadowsTheSelectedLights()
    {
        var (env, added) = Points(24);
        var spots = new SpotLight[LightEnvironment.MaxSpot + 6];
        for (var n = 0; n < spots.Length; n++)
            env.AddLight(spots[n] = new SpotLight { Position = new Vector3(n * 10f, 5, 0), Direction = -Vector3.UnitY, Range = 8f });
        var camera = new PerspectiveCamera
        {
            Position = new Vector3(230, 3, 8),
            Forward = -Vector3.UnitZ,
            AspectRatio = 16f / 9f,
            FieldOfView = 60f,
            Near = 0.1f,
            Far = 500f,
        };

        env.SelectLights(camera.Position);
        var planner = new ShadowPlanner();
        planner.Plan(env, camera, Aabb.Empty);

        var cubes = planner.Passes.ToArray().Where(p => p.Kind == ShadowPassKind.CubeFace).ToList();
        var tiles = planner.Passes.ToArray().Where(p => p.Kind == ShadowPassKind.AtlasTile).ToList();
        Assert.Equal(ShaderLimits.MaxShadowPoint * 6, cubes.Count);
        Assert.Equal(LightEnvironment.MaxSpot, tiles.Count);
        var ubo = UboPointPositions(env, camera.Position);
        foreach (var pass in cubes)
        {
            // The cube's light is in the UBO at the index the shader looks its shadow up with.
            Assert.InRange(pass.LightIndex, 0, LightEnvironment.MaxPoint - 1);
            Assert.Equal(ubo[pass.LightIndex], pass.LightPosition);
            Assert.Contains(env.PointLights[pass.LightIndex], added[8..24]);
        }

        // Spot tiles likewise come from the selected (nearest) spot lights, 6–13.
        Assert.All(tiles, pass => Assert.Contains(env.SpotLights[pass.LightIndex], spots[6..]));
    }

    [Fact]
    public void SelectingAllocatesNothing()
    {
        var (env, _) = Points(40);
        for (var n = 0; n < 20; n++)
            env.AddLight(new SpotLight { Position = new Vector3(n * 20f, 5, 0) });
        env.SelectLights(Vector3.Zero);

        var bytes = AllocationGate.SmallestWindow(() =>
        {
            for (var frame = 0; frame < 1000; frame++)
                env.SelectLights(new Vector3(frame % 400, 0, 0)); // sweeps the camera over every light
        });

        Assert.Equal(0, bytes);
    }
}
