using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0124: Godot 4.7's tonemap and glow settings on WorldEnvironment.</summary>
public sealed class PostProcessSettingsTests
{
    [Fact]
    public void DefaultsAreGodot47s()
    {
        var s = PostProcessSettings.Default;
        Assert.Equal(Tonemapper.Engine, s.Tonemapper);
        Assert.Equal(1f, s.TonemapExposure);
        Assert.Equal(1f, s.TonemapWhite);
        Assert.False(s.GlowEnabled);
        Assert.Equal([0f, 0.8f, 0.4f, 0.1f, 0f, 0f, 0f], [s.GlowLevel1, s.GlowLevel2, s.GlowLevel3, s.GlowLevel4, s.GlowLevel5, s.GlowLevel6, s.GlowLevel7]);
        Assert.False(s.GlowNormalized);
        Assert.Equal(0.3f, s.GlowIntensity);
        Assert.Equal(1f, s.GlowStrength);
        Assert.Equal(0.05f, s.GlowMix);
        Assert.Equal(0f, s.GlowBloom);
        Assert.Equal(GlowBlendMode.Screen, s.GlowBlendMode);
        Assert.Equal(1f, s.GlowHdrThreshold);
        Assert.Equal(2f, s.GlowHdrScale);
        Assert.Equal(12f, s.GlowHdrLuminanceCap);
    }

    [Fact]
    public void BlendModesKeepGodotsOrdinals()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<GlowBlendMode>().Select(m => (int)m));
    }

    [Fact]
    public void NormalizedWeightsDivideByTheSum()
    {
        Span<float> w = stackalloc float[PostProcessSettings.GlowLevelCount];
        (PostProcessSettings.Default with { GlowNormalized = true }).GetGlowWeights(w);
        Assert.Equal(0f, w[0]);
        Assert.Equal(0.8f / 1.3f, w[1], 1e-6f);
        Assert.Equal(0.4f / 1.3f, w[2], 1e-6f);
        Assert.Equal(0.1f / 1.3f, w[3], 1e-6f);

        PostProcessSettings.Default.GetGlowWeights(w);
        Assert.Equal(0.8f, w[1]); // as set when not normalized
    }

    [Fact]
    public void MaxLevelIsTheHighestWeightAboveOnePercent()
    {
        Assert.Equal(-1, PostProcessSettings.Default.GlowMaxLevel); // glow off
        var on = PostProcessSettings.Default with { GlowEnabled = true };
        Assert.Equal(3, on.GlowMaxLevel);
        Assert.Equal(0, (on with { GlowLevel2 = 0, GlowLevel3 = 0, GlowLevel4 = 0.005f, GlowLevel1 = 1 }).GlowMaxLevel);
        Assert.Equal(-1, (on with { GlowLevel2 = 0, GlowLevel3 = 0, GlowLevel4 = 0 }).GlowMaxLevel);
    }

    [Fact]
    public void GodotAcesWhiteTonemappedIsTheCurveAtTheBiasedWhite()
    {
        // Godot's environment_get_tonemap_parameters for ACES: white = max(1, white) · 1.8 through the fit.
        Assert.Equal(0.78107f, PostProcessSettings.Default.GodotAcesWhiteTonemapped, 1e-4f);
        Assert.Equal(PostProcessSettings.Default.GodotAcesWhiteTonemapped, (PostProcessSettings.Default with { TonemapWhite = 0.5f }).GodotAcesWhiteTonemapped);
        Assert.Equal(1f, PostProcessSettings.Default.GlowWhite);
        Assert.Equal(2f, (PostProcessSettings.Default with { Tonemapper = Tonemapper.GodotAces, TonemapWhite = 2f }).GlowWhite);
    }

    [Fact]
    public void LevelExtentsHalveFromHalfTheScene()
    {
        var scene = new Silk.NET.Vulkan.Extent2D(1920, 1080);
        Assert.Equal((960u, 540u), Size(GlowEffect.LevelExtent(scene, 0)));
        Assert.Equal((120u, 67u), Size(GlowEffect.LevelExtent(scene, 3)));
        Assert.Equal((15u, 8u), Size(GlowEffect.LevelExtent(scene, 6)));
        Assert.Equal((1u, 1u), Size(GlowEffect.LevelExtent(new Silk.NET.Vulkan.Extent2D(64, 3), 6)));
    }

    private static (uint, uint) Size(Silk.NET.Vulkan.Extent2D e) => (e.Width, e.Height);

    [Fact]
    public void WorldEnvironmentExportsRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.GodotAces,
                TonemapExposure = 1.2f,
                GlowEnabled = true,
                GlowNormalized = true,
                GlowStrength = 0.75f,
                GlowBlendMode = GlowBlendMode.Additive,
                GlowLevel5 = 0.25f,
            },
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        Assert.Equal(env.PostProcessSettings, copy.PostProcessSettings);
        Assert.Equal(Tonemapper.GodotAces, copy.PostProcessSettings.Tonemapper);
        Assert.Equal(0.25f, copy.PostProcess!.GlowLevel5);
    }
}
