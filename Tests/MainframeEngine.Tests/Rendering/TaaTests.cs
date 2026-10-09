using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0166: TAA's settings, its place among the post effects, the jitter it resolves with and its depth math.</summary>
public sealed class TaaTests
{
    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    private static PostEffectSettings With(AntiAliasing antiAliasing, float sharpness = IVulkanContext.DefaultTaaSharpness) =>
        PostEffectSettings.Default with { AntiAliasing = antiAliasing, TaaSharpness = sharpness };

    [Fact]
    public void TaaIsAnAntiAliasingModeAndItsSharpnessDefaultsEverywhere()
    {
        Assert.Equal(["None", "Fxaa", "Taa"], Enum.GetNames<AntiAliasing>());
        Assert.Equal(0.25f, IVulkanContext.DefaultTaaSharpness);
        Assert.Equal(IVulkanContext.DefaultTaaSharpness, new EngineOptions { GameName = "t" }.TaaSharpness);
        Assert.Equal(IVulkanContext.DefaultTaaSharpness, new ProjectSettings().Rendering.TaaSharpness);
        Assert.Equal(0f, PostEffectSettings.Default.TaaSharpness);
    }

    [Fact]
    public void TheTaaProjectSettingsRoundTrip()
    {
        var settings = new ProjectSettings();
        settings.Rendering.AntiAliasing = AntiAliasing.Taa;
        settings.Rendering.TaaSharpness = 0.5f;
        var json = Encoding.UTF8.GetString(settings.ToJson());
        Assert.Contains("\"antiAliasing\": \"Taa\"", json, StringComparison.Ordinal);
        Assert.Contains("\"taaSharpness\": 0.5", json, StringComparison.Ordinal);
        var parsed = Parse(json);
        Assert.Equal(AntiAliasing.Taa, parsed.Rendering.AntiAliasing);
        Assert.Equal(0.5f, parsed.Rendering.TaaSharpness);
        Assert.Equal(AntiAliasing.Taa, Parse("""{ "format": 2, "rendering": { "antiAliasing": "taa" } }""").Rendering.AntiAliasing);

        // The default sharpness is not written; out of range is an error naming the key.
        Assert.DoesNotContain("taaSharpness", Encoding.UTF8.GetString(new ProjectSettings().ToJson()), StringComparison.Ordinal);
        var error = Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 2, "rendering": { "taaSharpness": 2 } }"""));
        Assert.Contains("rendering.taaSharpness", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSettings().Rendering.TaaSharpness = -0.1f);
    }

    [Fact]
    public void TaaRunsFirstBeforeTheTonemapAndReplacesFxaa()
    {
        var autoExposure = new AutoExposure();
        var taa = new TaaEffect();
        var sharpen = new TaaSharpenEffect();
        var fxaa = new FxaaEffect();
        var stack = new PostProcessStack();
        foreach (PostEffect effect in new PostEffect[] { fxaa, new GlowEffect(autoExposure), sharpen, autoExposure, taa })
            stack.Add(effect);
        Assert.Same(taa, stack.Effects[0]); // everything after it reads the resolved, jitter-free image
        Assert.Equal(PostStage.BeforeTonemap, taa.Stage);
        Assert.Equal((PostStage.AfterTonemap, PostEffectOrder.Sharpen), (sharpen.Stage, sharpen.Order));

        // TAA needs the motion vectors (and so the prepass) and the jitter; FXAA does not run with it.
        Assert.Equal(PostEffectNeeds.Velocity | PostEffectNeeds.Jitter, taa.Needs);
        Assert.Equal(PostEffectNeeds.DepthPrepass | PostEffectNeeds.Velocity | PostEffectNeeds.Jitter, stack.GetNeeds(With(AntiAliasing.Taa)));
        Assert.True(taa.IsEnabled(With(AntiAliasing.Taa)));
        Assert.False(fxaa.IsEnabled(With(AntiAliasing.Taa)));
        Assert.False(taa.IsEnabled(With(AntiAliasing.Fxaa)));
        Assert.False(taa.IsEnabled(PostEffectSettings.Default));
        Assert.Equal(PostEffectNeeds.None, stack.GetNeeds(With(AntiAliasing.Fxaa)));

        // The sharpen is TAA's, and only with a sharpness.
        Assert.True(sharpen.IsEnabled(With(AntiAliasing.Taa)));
        Assert.False(sharpen.IsEnabled(With(AntiAliasing.Taa, 0f)));
        Assert.False(sharpen.IsEnabled(With(AntiAliasing.Fxaa)));
        Assert.Equal(1, stack.CountEnabled(PostStage.AfterTonemap, With(AntiAliasing.Taa)));
        Assert.Equal(0, stack.CountEnabled(PostStage.AfterTonemap, With(AntiAliasing.Taa, 0f)));
        Assert.Equal(1, stack.CountEnabled(PostStage.BeforeTonemap, With(AntiAliasing.Taa))); // the default world: TAA alone
    }

    [Fact]
    public void TheResolveSeesTheJitterSequenceInPixels()
    {
        // The render server jitters by TemporalJitter.NdcOffset; the resolve's pixel offset is exactly the sample's
        // PixelOffset (x right, y down) at any size, for every sample of the cycle.
        foreach (var (width, height) in new[] { (1920u, 1080u), (320u, 240u), (7u, 3u) })
        {
            for (var frame = 0UL; frame < 2 * TemporalJitter.DefaultSampleCount; frame++)
            {
                var index = TemporalJitter.SampleIndex(frame);
                var ndc = TemporalJitter.NdcOffset(index, width, height);
                var p = TaaEffect.Params(Camera(Vector3.Zero, Vector3.Zero) with { Jitter = ndc }, new Extent2D(width, height), 1.3f, true);
                var expected = TemporalJitter.PixelOffset(index);
                Assert.Equal(expected.X, p.JitterPixels.X, 4);
                Assert.Equal(expected.Y, p.JitterPixels.Y, 4);
                Assert.Equal(ndc, p.JitterNdc);
            }
        }
    }

    [Fact]
    public void TheParamsCarryTheHistoryStateAndTheBlend()
    {
        var valid = TaaEffect.Params(Camera(Vector3.Zero, Vector3.Zero), new Extent2D(640, 480), 1.3f, historyValid: true);
        var reset = TaaEffect.Params(Camera(Vector3.Zero, Vector3.Zero), new Extent2D(640, 480), float.NaN, historyValid: false);
        Assert.Equal(TaaEffect.FlagHistoryValid | TaaEffect.FlagDepthRejection, valid.Flags);
        Assert.Equal(TaaEffect.FlagDepthRejection, reset.Flags);
        Assert.Equal(IVulkanContext.DefaultExposure, reset.Exposure); // never a NaN weighting
        Assert.Equal(new Vector2(640f, 480f), valid.OutputSize);
        Assert.Equal(new Vector2(1f / 640f, 1f / 480f), valid.RcpOutputSize);
        Assert.InRange(TaaEffect.FeedbackMoving, 0.8f, TaaEffect.FeedbackStill);
        Assert.InRange(TaaEffect.FeedbackStill, 0.85f, 0.97f);
        Assert.Equal(96, Marshal.SizeOf<TaaParams>()); // Taa.vk.frag's push block
    }

    [Fact]
    public void TheDepthRowsGiveAPointsViewDepthNowAndLastFrame()
    {
        // The camera moved 0.5 m forward and turned 4°: a point's depth now and in last frame's view, from its NDC and depth.
        var camera = Camera(new Vector3(0f, 1.7f, -0.5f), new Vector3(0f, 1.7f, 0f), yawDegrees: 4f);
        var p = TaaEffect.Params(camera, new Extent2D(1280, 720), 1.3f, true);
        foreach (var world in new[] { new Vector3(0.4f, 1.2f, -6f), new Vector3(-3f, 0.2f, -25f), new Vector3(1f, 4f, -90f) })
        {
            var clip = Vector4.Transform(new Vector4(world, 1f), camera.ViewProjection);
            var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            var (now, previous) = TaaEffect.ViewDepths(p, ndc);
            var previousClip = Vector4.Transform(new Vector4(world, 1f), camera.PreviousViewProjection);
            Assert.Equal(clip.W, now, clip.W * 1e-3f);
            Assert.Equal(previousClip.W, previous, previousClip.W * 1e-3f);
            Assert.True(MathF.Abs(now - previous) > 0.3f, "the camera moved: the depths must differ");
        }
    }

    [Fact]
    public void AFoliageMaterialPacksItsDitherOnlyForCutouts()
    {
        Assert.False(new FoliageMaterial3D().AlphaDither);
        Assert.Equal(0f, MaterialParams.From(new FoliageMaterial3D(), 0).Pbr.W);
        Assert.Equal(1f, MaterialParams.From(new FoliageMaterial3D { AlphaDither = true }, 0).Pbr.W);
        Assert.Equal(0f, MaterialParams.From(new FoliageMaterial3D { AlphaDither = true, AlphaCutout = false }, 0).Pbr.W);
        Assert.Equal(0f, MaterialParams.From(new StandardMaterial3D { Transparency = AlphaMode.Cutout }, 0).Pbr.W);

        // Changing it re-uploads the material.
        var material = new FoliageMaterial3D();
        var version = material.Version;
        material.AlphaDither = true;
        Assert.NotEqual(version, material.Version);
    }

    // A camera looking down −Z from `position` (rotated by `yawDegrees`), last frame at `previous` looking down −Z.
    private static PostCamera Camera(Vector3 position, Vector3 previous, float yawDegrees = 0f)
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 16f / 9f, 0.1f, 400f);
        var forward = Vector3.Transform(-Vector3.UnitZ, Quaternion.CreateFromAxisAngle(Vector3.UnitY, float.DegreesToRadians(yawDegrees)));
        var view = Matrix4x4.CreateLookAt(position, position + forward, Vector3.UnitY);
        var previousView = Matrix4x4.CreateLookAt(previous, previous - Vector3.UnitZ, Vector3.UnitY);
        return new PostCamera(view, projection, projection, view * projection, previousView * projection, Vector2.Zero, Vector2.Zero,
            true, position, 0.1f, 400f);
    }
}
