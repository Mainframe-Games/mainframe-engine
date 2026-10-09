using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>
/// ADR 0174: render-resolution scaling — the render size, the Halton cycle, the mip bias, the settings and their round
/// trip, which effects run at which resolution, the spatial upscale and FSR's RCAS, and TAAU's push block.
/// </summary>
public sealed class TaauTests
{
    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    [Theory]
    [InlineData(2560u, 1440u, 0.75f, 1920u, 1080u)]
    [InlineData(1920u, 1080u, 0.75f, 1440u, 810u)]
    [InlineData(1920u, 1080u, 0.5f, 960u, 540u)]
    [InlineData(1920u, 1080u, 1f, 1920u, 1080u)]
    [InlineData(1921u, 1081u, 0.5f, 961u, 541u)] // halves round up
    [InlineData(3u, 2u, 0.25f, 1u, 1u)]          // at least a pixel
    [InlineData(1920u, 1080u, 0.1f, 480u, 270u)] // clamped to 0.25
    [InlineData(1920u, 1080u, 1.5f, 1920u, 1080u)] // no supersampling
    public void TheRenderSizeIsTheOutputTimesTheScale(uint width, uint height, float scale, uint renderWidth, uint renderHeight)
    {
        var render = RenderScaling.RenderExtent(new Extent2D(width, height), scale);
        Assert.Equal((renderWidth, renderHeight), (render.Width, render.Height));
    }

    [Fact]
    public void TheHaltonCycleCoversEveryOutputPixel()
    {
        // 8 / scale², rounded up to a power of two: 8 samples at native, 16 at 0.75 (14.2), 32 at 0.5, 128 at 0.25.
        Assert.Equal(8, RenderScaling.JitterSampleCount(1f));
        Assert.Equal(16, RenderScaling.JitterSampleCount(0.75f));
        Assert.Equal(16, RenderScaling.JitterSampleCount(0.71f)); // 15.9
        Assert.Equal(32, RenderScaling.JitterSampleCount(0.7f));  // 16.3
        Assert.Equal(32, RenderScaling.JitterSampleCount(0.5f));
        Assert.Equal(128, RenderScaling.JitterSampleCount(0.25f));
        Assert.Equal(TemporalJitter.DefaultSampleCount, RenderScaling.JitterSampleCount(float.NaN));
        for (var scale = 0.25f; scale <= 1f; scale += 0.01f)
        {
            var count = RenderScaling.JitterSampleCount(scale);
            Assert.True((count & (count - 1)) == 0, $"{count} at {scale} is not a power of two");
            Assert.True(count * scale * scale >= TemporalJitter.DefaultSampleCount - 1e-3f, $"{count} samples at {scale}");
        }

        // The 16 samples at 0.75 are distinct and fill every quarter of the render pixel: each output pixel sees samples
        // on all sides over a cycle.
        var quarters = new int[4];
        var seen = new HashSet<Vector2>();
        for (var i = 0; i < 16; i++)
        {
            var p = TemporalJitter.PixelOffset(i);
            Assert.True(seen.Add(p));
            quarters[(p.X < 0f ? 0 : 1) + (p.Y < 0f ? 0 : 2)]++;
        }

        Assert.All(quarters, q => Assert.InRange(q, 3, 5));
        Assert.Equal(15, TemporalJitter.SampleIndex(31, RenderScaling.JitterSampleCount(0.75f)));
    }

    [Fact]
    public void TheMipBiasIsTheLogOfTheScale()
    {
        Assert.Equal(0f, RenderScaling.MipBias(1f));
        Assert.Equal(-0.415f, RenderScaling.MipBias(0.75f), 3);
        Assert.Equal(-1f, RenderScaling.MipBias(0.5f), 6);
        Assert.Equal(-2f, RenderScaling.MipBias(0.1f), 6); // clamped to 0.25
        Assert.Equal(0.75f, RenderScaling.ScaleOf(new Extent2D(1920, 1080), new Extent2D(2560, 1440)));
        Assert.Equal(1f, RenderScaling.ScaleOf(new Extent2D(1920, 1080), new Extent2D(1920, 1080)));
        Assert.Equal(1f, RenderScaling.ScaleOf(default, default));

        // The frame block carries it after the probe fields; every block starts at native (bias 0, gradient scale 1).
        Assert.Equal(672, (int)Marshal.OffsetOf<FrameData>(nameof(FrameData.MipBias)));
        Assert.Equal(676, (int)Marshal.OffsetOf<FrameData>(nameof(FrameData.MipScale)));
        var data = FrameData.From(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 10f), Vector3.Zero,
            new Extent2D(8, 8), 0f, 1f);
        Assert.Equal((0f, 1f), (data.MipBias, data.MipScale));
    }

    [Fact]
    public void TheScalingSettingsUseGodotsNamesAndRoundTrip()
    {
        var defaults = new ProjectSettings().Rendering;
        Assert.Equal(Scaling3DMode.Bilinear, defaults.Scaling3DMode);
        Assert.Equal(1f, defaults.Scaling3DScale);
        Assert.Equal(0.2f, defaults.FsrSharpness);
        Assert.Equal(["Bilinear", "Fsr", "Taau"], Enum.GetNames<Scaling3DMode>());
        var empty = Encoding.UTF8.GetString(new ProjectSettings().ToJson());
        Assert.DoesNotContain("scaling3D", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("fsrSharpness", empty, StringComparison.Ordinal);

        var settings = new ProjectSettings();
        settings.Rendering.Scaling3DMode = Scaling3DMode.Taau;
        settings.Rendering.Scaling3DScale = 0.75f;
        settings.Rendering.FsrSharpness = 0.5f;
        var json = Encoding.UTF8.GetString(settings.ToJson());
        Assert.Contains("\"scaling3DMode\": \"Taau\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scaling3DScale\": 0.75", json, StringComparison.Ordinal);
        Assert.Contains("\"fsrSharpness\": 0.5", json, StringComparison.Ordinal);
        var parsed = Parse(json);
        Assert.Equal((Scaling3DMode.Taau, 0.75f, 0.5f),
            (parsed.Rendering.Scaling3DMode, parsed.Rendering.Scaling3DScale, parsed.Rendering.FsrSharpness));

        // Older files (format 2 before ADR 0174) need no migration: the keys are optional and default to native.
        var old = Parse("""{ "format": 2, "rendering": { "antiAliasing": "Taa" } }""");
        Assert.Equal((Scaling3DMode.Bilinear, 1f), (old.Rendering.Scaling3DMode, old.Rendering.Scaling3DScale));
        Assert.Equal(Scaling3DMode.Fsr, Parse("""{ "format": 2, "rendering": { "scaling3DMode": "fsr" } }""").Rendering.Scaling3DMode);

        // Out of range is an error naming the key.
        Assert.Contains("rendering.scaling3DScale",
            Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 2, "rendering": { "scaling3DScale": 0.1 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("rendering.fsrSharpness",
            Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 2, "rendering": { "fsrSharpness": 3 } }""")).Message, StringComparison.Ordinal);
        Assert.Contains("rendering.scaling3DMode",
            Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 2, "rendering": { "scaling3DMode": "Dlss" } }""")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProjectSettings().Rendering.Scaling3DScale = 1.01f);
    }

    [Fact]
    public void EffectsBeforeTheUpscaleRunAtRenderResolutionAndTheRestAtOutputResolution()
    {
        var autoExposure = new AutoExposure();
        var ssao = new SsaoEffect();
        PostEffect[] render = [ssao, new ContactShadows(), new VolumetricFogEffect(), new DepthOfFieldEffect(), new TaaEffect()];
        PostEffect[] output =
        [
            new SpatialUpscaleEffect(), autoExposure, new GlowEffect(autoExposure), new LightShafts(), new TaaSharpenEffect(),
            new FxaaEffect(), new ColorGradeEffect(), new VelocityDebugView(ssao),
        ];
        Assert.All(render, e => Assert.False(PostEffectContext.RunsAtOutputResolution(e), e.ToString()));
        Assert.All(output, e => Assert.True(PostEffectContext.RunsAtOutputResolution(e), e.ToString()));

        // Depth of field moved before TAA (render resolution); the spatial upscale comes right after it.
        var stack = new PostProcessStack();
        foreach (var effect in output.Concat(render))
            stack.Add(effect);
        var before = stack.Effects.Where(e => e.Stage == PostStage.BeforeTonemap).Select(e => e.GetType().Name).ToArray();
        Assert.Equal(["VolumetricFogEffect", "DepthOfFieldEffect", "TaaEffect", "SpatialUpscaleEffect", "AutoExposure", "GlowEffect", "LightShafts"], before);

        // A context without images (tests) ignores the selection; one with them switches the colour and size per effect.
        var context = new PostEffectContext();
        context.Scene.SetResolutions(new Extent2D(1440, 810), new ImageView(1), new Extent2D(1920, 1080), new ImageView(2));
        Assert.True(context.Scene.Upscaled);
        context.Enter(render[3]);
        Assert.Equal((1440u, 1ul), (context.Scene.Extent.Width, context.Scene.Color.Handle));
        Assert.False(context.IsOutputResolution);
        context.Enter(output[1]);
        Assert.Equal((1920u, 2ul), (context.Scene.Extent.Width, context.Scene.Color.Handle));
        Assert.True(context.IsOutputResolution);
    }

    [Fact]
    public void TheSpatialUpscaleRunsWhileUpscalingAndFsrAddsItsSharpen()
    {
        var upscale = new SpatialUpscaleEffect();
        var rcas = new TaaSharpenEffect();
        var native = PostEffectSettings.Default with { Scaling3DMode = Scaling3DMode.Fsr, FsrSharpness = 0.2f };
        var fsr = native with { Upscaling = true };
        var bilinear = fsr with { Scaling3DMode = Scaling3DMode.Bilinear };
        var taau = fsr with { AntiAliasing = AntiAliasing.Taa, TaaSharpness = 0.25f };

        Assert.False(upscale.IsEnabled(native));
        Assert.True(upscale.IsEnabled(fsr));
        Assert.True(upscale.IsEnabled(taau)); // records only when the resolve skipped a frame
        Assert.Equal(SpatialUpscaleEffect.ModeEasu, SpatialUpscaleEffect.ModeFor(fsr));
        Assert.Equal(SpatialUpscaleEffect.ModeBilinear, SpatialUpscaleEffect.ModeFor(bilinear));
        Assert.Equal(SpatialUpscaleEffect.ModeBilinear, SpatialUpscaleEffect.ModeFor(taau)); // TAA upscales itself
        Assert.Equal(PostEffectNeeds.None, upscale.Needs);

        // RCAS: FSR's second half with its stops (exp2(-0.2) at Godot's default), TAA's sharpen otherwise; not with bilinear.
        Assert.False(rcas.IsEnabled(native));
        Assert.True(rcas.IsEnabled(fsr));
        Assert.False(rcas.IsEnabled(bilinear));
        Assert.Equal(MathF.Pow(2f, -0.2f), TaaSharpenEffect.SharpnessFor(fsr), 5);
        Assert.Equal(1f, TaaSharpenEffect.SharpnessFor(fsr with { FsrSharpness = 0f }));
        Assert.Equal(0.25f, TaaSharpenEffect.SharpnessFor(fsr with { FsrSharpness = 2f }));
        Assert.True(rcas.IsEnabled(taau));
        Assert.Equal(0.25f, TaaSharpenEffect.SharpnessFor(taau));
    }

    [Fact]
    public void TaausPushBlockMeasuresTheJitterInRenderPixelsAndTheHistoryInOutputPixels()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 100f);
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 1, 5), Vector3.Zero, Vector3.UnitY);
        var render = new Extent2D(1440, 810);
        var output = new Extent2D(1920, 1080);
        var jitter = TemporalJitter.NdcOffset(3, render.Width, render.Height);
        var camera = new PostCamera(view, projection, TemporalJitter.Apply(projection, jitter), view * projection, view * projection,
            jitter, default, true, new Vector3(0, 1, 5), 0.1f, 100f);

        var native = TaaEffect.Params(camera, output, 1f, true);
        var taau = TaaEffect.Params(camera, render, output, 1f, true);
        Assert.Equal(0u, native.Flags & TaaEffect.FlagUpscale);
        Assert.Equal(TaaEffect.FlagUpscale, taau.Flags & TaaEffect.FlagUpscale);
        Assert.Equal(new Vector2(1920, 1080), taau.OutputSize);
        var pixels = TemporalJitter.PixelOffset(3);
        Assert.Equal(pixels.X, taau.JitterPixels.X, 4); // in render pixels: the offset Halton gave
        Assert.Equal(pixels.Y, taau.JitterPixels.Y, 4);
        Assert.Equal(TaaEffect.FilterFalloff, native.FilterFalloff);

        // The reconstruction narrows with the input's spacing (0.75 / ratio² output pixels: 0.42 at a scale of 0.75).
        Assert.Equal(TaaEffect.UpscaleFilterFalloff(4f / 3f), taau.FilterFalloff);
        Assert.Equal(-2.29f / (0.421875f * 0.421875f), taau.FilterFalloff, 3);
        Assert.Equal(TaaEffect.FilterFalloff, TaaEffect.UpscaleFilterFalloff(1f));

        // The push block is still 96 bytes.
        Assert.Equal(96, Marshal.SizeOf<TaaParams>());
    }
}
