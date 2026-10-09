using System.Numerics;
using MainframeEngine.Serialization;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// ADR 0171: the volumetric fog's settings on <see cref="WorldEnvironment"/> (Godot's names and defaults), their packing,
/// the analytic fog's start distance, the effect's stage and enable rule, the phase function, the step spacing, the
/// noise volume and the march's push block.
/// </summary>
public sealed class VolumetricFogTests
{
    [Fact]
    public void DefaultsAreOffWithGodotsValues()
    {
        var env = new WorldEnvironment();
        Assert.False(env.VolumetricFogEnabled);
        Assert.Equal(0.05f, env.VolumetricFogDensity);
        Assert.Equal(Vector3.One, env.VolumetricFogAlbedo);
        Assert.Equal(Vector3.Zero, env.VolumetricFogEmission);
        Assert.Equal(1f, env.VolumetricFogEmissionEnergy);
        Assert.Equal(0.2f, env.VolumetricFogAnisotropy);
        Assert.Equal(64f, env.VolumetricFogLength);
        Assert.Equal(2f, env.VolumetricFogDetailSpread);
        Assert.Equal(0f, env.VolumetricFogAmbientInject);
        Assert.Equal(1f, env.VolumetricFogSkyAffect);
        Assert.True(env.VolumetricFogTemporalReprojectionEnabled);
        Assert.Equal(0.9f, env.VolumetricFogTemporalReprojectionAmount);
        Assert.Equal(0f, env.VolumetricFogNoiseStrength);
        Assert.False(env.VolumetricFogSettings.Active);
        Assert.Equal(default, PostEffectSettings.Default.VolumetricFog);
    }

    [Fact]
    public void TheSettingsPackLinearColoursAndTheFogsHeightProfile()
    {
        var env = new WorldEnvironment
        {
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.02f,
            VolumetricFogAlbedo = new Vector3(0.5f, 1f, 0f),
            VolumetricFogEmission = new Vector3(1f, 0.5f, 0f),
            VolumetricFogEmissionEnergy = 2f,
            VolumetricFogAnisotropy = 0.99f,
            FogHeight = 6f,
            FogHeightDensity = 0.08f,
        };
        var s = env.VolumetricFogSettings;
        Assert.True(s.Active);
        Assert.Equal(ColorSpace.SrgbToLinear(new Vector3(0.5f, 1f, 0f)), s.Albedo);
        Assert.Equal(ColorSpace.SrgbToLinear(new Vector3(1f, 0.5f, 0f)) * 2f, s.Emission);
        Assert.Equal(0.95f, s.Anisotropy); // clamped: the phase function stays finite
        Assert.Equal((6f, 0.08f), (s.Height, s.HeightDensity));

        env.VolumetricFogDensity = 0f;
        Assert.False(env.VolumetricFogSettings.Active);
        env.VolumetricFogDensity = 0.02f;
        env.VolumetricFogLength = 0f;
        Assert.False(env.VolumetricFogSettings.Active);
    }

    [Fact]
    public void TheAnalyticFogStartsWhereTheMarchEndsOnlyInAViewThatRunsIt()
    {
        var env = new WorldEnvironment { FogEnabled = true, VolumetricFogLength = 48f };
        Assert.Equal(1f, env.FrameEnvironment.FogColor.W);
        Assert.Equal(1f, env.GetFrameEnvironment(volumetricFog: true).FogColor.W); // volumetric fog off: from the camera

        env.VolumetricFogEnabled = true;
        Assert.Equal(1f, env.FrameEnvironment.FogColor.W); // other views (no post-processing) keep the whole integral
        Assert.Equal(49f, env.GetFrameEnvironment(volumetricFog: true).FogColor.W);

        env.FogEnabled = false;
        Assert.Equal(0f, env.GetFrameEnvironment(volumetricFog: true).FogColor.W); // still off
    }

    [Fact]
    public void TheEffectRunsFirstBeforeTheTonemapWithoutAPrepass()
    {
        using var effect = new VolumetricFogEffect();
        Assert.Equal(PostStage.BeforeTonemap, effect.Stage);
        Assert.True(effect.Order < PostEffectOrder.Taa); // TAA smooths the composited fog with the scene
        Assert.Equal(PostEffectNeeds.None, effect.Needs);

        var off = PostEffectSettings.Default;
        Assert.False(effect.IsEnabled(off));
        var env = new WorldEnvironment { VolumetricFogEnabled = true };
        Assert.True(effect.IsEnabled(off with { VolumetricFog = env.VolumetricFogSettings }));
        Assert.False(off.PostTonemap); // it composites into the scene colour: the engine tonemap is enough
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    [InlineData(-0.5f)]
    public void HenyeyGreensteinIntegratesToOneOverTheSphere(float g)
    {
        // ∫ p(cos θ) dω = 2π ∫_{-1}^{1} p(μ) dμ, midpoint rule.
        const int n = 20000;
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var mu = -1f + (i + 0.5f) * 2f / n;
            sum += VolumetricFogEffect.HenyeyGreenstein(g, mu) * (2.0 / n);
        }

        Assert.Equal(1.0, 2 * Math.PI * sum, 3);
        if (g > 0f)
            Assert.True(VolumetricFogEffect.HenyeyGreenstein(g, 1f) > VolumetricFogEffect.HenyeyGreenstein(g, -1f)); // forwards
    }

    [Fact]
    public void IsotropicScatteringIsOneOverFourPi() =>
        Assert.Equal(1f / (4f * MathF.PI), VolumetricFogEffect.HenyeyGreenstein(0f, 0.3f), 6);

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(3.5f)]
    public void StepsCoverTheRayAndCrowdTowardsTheCamera(float spread)
    {
        const int steps = VolumetricFogEffect.Steps;
        var previous = 0f;
        var previousLength = 0f;
        for (var i = 0; i < steps; i++)
        {
            var end = VolumetricFogEffect.StepEnd(i, steps, 64f, spread);
            var length = end - previous;
            Assert.True(length > 0f);
            if (spread > 1f && i > 0)
                Assert.True(length >= previousLength); // later steps are longer
            previous = end;
            previousLength = length;
        }

        Assert.Equal(64f, previous, 4);
        if (spread == 1f)
            Assert.Equal(64f / steps, VolumetricFogEffect.StepEnd(0, steps, 64f, spread), 4);
    }

    [Fact]
    public void TheNoiseVolumeIsDeterministicFullRangeAndTiles()
    {
        var noise = VolumetricFogEffect.Noise();
        const int n = VolumetricFogEffect.NoiseSize;
        Assert.Equal(n * n * n * 4, noise.Length);
        Assert.Same(noise, VolumetricFogEffect.Noise());
        byte min = 255, max = 0;
        double sum = 0;
        for (var i = 0; i < noise.Length; i += 4)
        {
            min = Math.Min(min, noise[i]);
            max = Math.Max(max, noise[i]);
            sum += noise[i];
        }

        Assert.Equal((byte)0, min);
        Assert.Equal((byte)255, max);
        var mean = sum / (n * n * n);
        Assert.InRange(mean, 90, 165); // the density's mean stays near the setting

        // It wraps: the last texel of a row is as close to the first as neighbours are to each other.
        double wrap = 0, inner = 0;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
            {
                var row = (z * n + y) * n * 4;
                wrap += Math.Abs(noise[row + (n - 1) * 4] - noise[row]);
                inner += Math.Abs(noise[row + 4] - noise[row]);
            }

        Assert.True(wrap < 2.5 * inner, $"the noise does not tile: {wrap / (n * n):F1} across the seam, {inner / (n * n):F1} inside");
    }

    [Fact]
    public void TheMarchPushBlockFitsTheFrameLayoutsRange()
    {
        Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<VolumetricFogEffect.MarchPush>() <= FrameContext.PushConstantSize);
        Assert.Equal(96, System.Runtime.CompilerServices.Unsafe.SizeOf<VolumetricFogEffect.MarchPush>());
        Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<VolumetricFogEffect.TemporalPush>() <= FrameContext.PushConstantSize);

        var env = new WorldEnvironment
        {
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.04f,
            VolumetricFogAnisotropy = 0.6f,
            VolumetricFogLength = 50f,
            VolumetricFogNoiseScale = 10f,
            VolumetricFogNoiseStrength = 0.4f,
            VolumetricFogAmbientInject = 0.5f,
            FogHeight = 3f,
            FogHeightDensity = 0.1f,
        };
        var push = VolumetricFogEffect.MarchParams(env.VolumetricFogSettings, new Extent2D(960, 540), new Extent2D(1920, 1080), 70);
        Assert.Equal(new Vector4(0.04f, 0.04f, 0.04f, 0.04f), push.Scattering);
        Assert.Equal(0.6f, push.Emission.W);
        Assert.Equal(new Vector4(50f, 2f, VolumetricFogEffect.Steps, 70 % 64), push.Ray);
        Assert.Equal(new Vector4(3f, 0.1f, 0.5f, 0f), push.Height);
        Assert.Equal(new Vector4(0.1f, 0.4f, 0f, 0f), push.Noise);
        Assert.Equal(new Vector4(960, 540, 1920, 1080), push.Sizes);
        Assert.Equal(-1f, VolumetricFogEffect.MarchParams(env.VolumetricFogSettings, default, default, null).Ray.W); // fixed noise
    }

    [Fact]
    public void WorldEnvironmentExportsRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.02f,
            VolumetricFogAlbedo = new Vector3(0.9f, 0.95f, 1f),
            VolumetricFogEmission = new Vector3(0.1f, 0.2f, 0.3f),
            VolumetricFogEmissionEnergy = 0.5f,
            VolumetricFogAnisotropy = 0.6f,
            VolumetricFogLength = 72f,
            VolumetricFogDetailSpread = 1.5f,
            VolumetricFogAmbientInject = 0.3f,
            VolumetricFogSkyAffect = 0.4f,
            VolumetricFogTemporalReprojectionEnabled = false,
            VolumetricFogTemporalReprojectionAmount = 0.85f,
            VolumetricFogNoiseScale = 12f,
            VolumetricFogNoiseStrength = 0.4f,
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        Assert.Equal(env.VolumetricFogSettings, copy.VolumetricFogSettings);
        Assert.Equal(env.VolumetricFogEmissionEnergy, copy.VolumetricFogEmissionEnergy);

        // Defaults are not written: scenes without volumetric fog do not change.
        var plain = new Node3D { Name = "Root" };
        var plainEnv = new WorldEnvironment { Name = "Environment" };
        plain.AddChild(plainEnv);
        plainEnv.Owner = plain;
        Assert.DoesNotContain("VolumetricFog", System.Text.Encoding.UTF8.GetString(SceneSaver.ToJson(plain)), StringComparison.Ordinal);
    }
}
