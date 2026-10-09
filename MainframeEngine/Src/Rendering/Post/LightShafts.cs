using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Where the sun is on screen for light shafts (ADR 0160): <see cref="ScreenUv"/> in scene-image UV (0,0 top-left,
/// 1,1 bottom-right; outside [0, 1] when the sun is off-screen) and <see cref="Fade"/>, 1 while the sun is on screen,
/// falling to 0 as it moves more than <see cref="OffscreenMargin"/> past an edge, and 0 behind the camera. The render
/// server sets it each frame (<see cref="IVulkanContext.LightShaftsSun"/>) from the tree's root camera and the world's
/// first <see cref="DirectionalLight3D"/>.
/// </summary>
public readonly record struct LightShaftsSun(Vector2 ScreenUv, float Fade)
{
    /// <summary>How far past a screen edge (in UV, so a fraction of the screen) the sun may go before the shafts are gone.</summary>
    public const float OffscreenMargin = 0.3f;

    /// <summary>
    /// The sun towards <paramref name="towardsSun"/> (a world direction, at infinity) seen through <paramref name="view"/>
    /// and <paramref name="projection"/> (row-vector matrices, the main pass's Y-up screen). A camera whose projection
    /// does not divide by depth (orthographic) never sees the sun: fade 0.
    /// </summary>
    public static LightShaftsSun Compute(in Matrix4x4 view, in Matrix4x4 projection, Vector3 towardsSun)
    {
        if (towardsSun.LengthSquared() < 1e-12f)
            return default;
        var clip = Vector4.Transform(new Vector4(Vector3.Normalize(towardsSun), 0f), view * projection);
        if (!(clip.W > 1e-4f))
            return default; // behind the camera, at 90° to it, or an orthographic camera
        // NDC +Y is up and lands on row 0 (the main pass flips the viewport), so v grows downwards like the image.
        var uv = new Vector2(clip.X / clip.W * 0.5f + 0.5f, 0.5f - clip.Y / clip.W * 0.5f);
        var outside = MathF.Max(MathF.Max(-uv.X, uv.X - 1f), MathF.Max(-uv.Y, uv.Y - 1f));
        var fade = 1f - SmoothStep(0f, OffscreenMargin, outside);
        if (!(fade > 0f))
            return default;
        return new LightShaftsSun(Vector2.Clamp(uv, new Vector2(-1f), new Vector2(2f)), fade);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Screen-space light shafts (ADR 0160; GPU Gems 3 ch. 13, Mitchell's radial blur) as three half-resolution raster
/// passes, recorded after the scene pass with no render pass active:
/// <list type="number">
/// <item><c>LightShaftsMask</c>: per half-resolution texel, the mean of its 2 × 2 scene texels that are sky (depth at
/// the far plane, the cleared 1), luminance-capped, weighted by a falloff around the sun's screen position.</item>
/// <item><c>LightShaftsBlur</c> twice: <see cref="PostProcessSettings.LightShaftsTapsPerPass"/> taps from each texel
/// towards the sun, fine steps then coarse ones (<see cref="PostProcessSettings.LightShaftsPass"/>), so a ray gets
/// <c>n²</c> effective taps. Samples past the image edge read black.</item>
/// </list>
/// The post tonemap pass samples <see cref="OutputView"/> (bilinear) and adds it, × intensity × fade, to the scene before
/// exposure, alongside glow. Every image is cleared once after creation, so the tonemap can bind the output on frames
/// that draw no shafts. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class LightShafts : IDisposable
{
    private const Format ShaftFormat = Format.R16G16B16A16Sfloat;

    /// <summary>Radius of the mask's falloff around the sun, in screen heights.</summary>
    public const float MaskRadius = 0.6f;

    /// <summary>Brightest luminance a sky texel contributes (scene radiance): the sun disc would otherwise dominate.</summary>
    public const float LuminanceCap = 8f;

    private readonly IVulkanContext _ctx;
    private readonly RenderTarget _mask;
    private readonly RenderTarget _fine;
    private readonly RenderTarget _coarse;
    private readonly Sampler _pointSampler;
    private readonly Sampler _blurSampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _maskSet;   // 0: scene colour, 1: scene depth
    private readonly DescriptorSet _fineSet;   // 0: mask
    private readonly DescriptorSet _coarseSet; // 0: fine
    private readonly PipelineLayout _layout;
    private readonly Pipeline _maskPipeline;
    private readonly Pipeline _blurPipeline;
    private bool _needsClear = true;
    private bool _disposed;

    public LightShafts(IVulkanContext ctx, Extent2D sceneExtent, ImageView sceneView, ImageView depthView)
    {
        _ctx = ctx;
        var size = TargetExtent(sceneExtent);
        _mask = new RenderTarget(ctx, new RenderTargetDesc("light shafts mask", [RenderTargetAttachment.Sampled(ShaftFormat)], null), size);
        _fine = new RenderTarget(ctx, new RenderTargetDesc("light shafts fine", [RenderTargetAttachment.Sampled(ShaftFormat)], null), size);
        _coarse = new RenderTarget(ctx, new RenderTargetDesc("light shafts", [RenderTargetAttachment.Sampled(ShaftFormat)], null), size);

        var point = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in point, null, out _pointSampler).Check("vkCreateSampler (light shafts mask)");
        // Bilinear, black past the edges: a ray leaving the image adds nothing (clamping would smear the edge texels).
        var blur = point with
        {
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatTransparentBlack,
        };
        ctx.Vk.CreateSampler(ctx.Device, in blur, null, out _blurSampler).Check("vkCreateSampler (light shafts blur)");

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "light shafts");
        _pool = PipelineBuilder.CreatePool(ctx, 3, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 6 }], "light shafts");
        _maskSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "light shafts mask");
        _fineSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "light shafts fine");
        _coarseSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "light shafts coarse");
        WriteSets(sceneView, depthView);

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(ShaftsPush), ShaderStageFlags.FragmentBit, "light shafts");
        _maskPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _mask.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/LightShaftsMask.vk.frag.spv", [], [], "light shafts mask");
        _blurPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _fine.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/LightShaftsBlur.vk.frag.spv", [], [], "light shafts blur");
    }

    /// <summary>The shafts the tonemap adds, in shader-read layout once <see cref="Record"/> has run.</summary>
    public ImageView OutputView => _coarse.GetColor(0).View;

    /// <summary>The bilinear sampler to read <see cref="OutputView"/> with.</summary>
    public Sampler Sampler => _blurSampler;

    /// <summary>Half the scene's size, at least one pixel.</summary>
    public static Extent2D TargetExtent(Extent2D scene) => new(Math.Max(1u, scene.Width / 2), Math.Max(1u, scene.Height / 2));

    /// <summary>After a swapchain resize (device idle): resized targets, rewritten sets; they are cleared again.</summary>
    public void Resize(Extent2D sceneExtent, ImageView sceneView, ImageView depthView)
    {
        var size = TargetExtent(sceneExtent);
        _mask.Resize(size);
        _fine.Resize(size);
        _coarse.Resize(size);
        WriteSets(sceneView, depthView);
        _needsClear = true;
    }

    /// <summary>
    /// Records the mask and the two blur passes for <paramref name="settings"/> and <paramref name="sun"/>. Returns false
    /// when nothing was drawn this frame (shafts off, or the sun out of reach): the tonemap must then add nothing.
    /// </summary>
    public bool Record(CommandBuffer cb, in PostProcessSettings settings, in LightShaftsSun sun)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_needsClear)
        {
            // The tonemap binds the output every frame: it must hold defined data in shader-read layout.
            _mask.Begin(cb, default);
            _mask.End(cb);
            _fine.Begin(cb, default);
            _fine.End(cb);
            _coarse.Begin(cb, default);
            _coarse.End(cb);
            _needsClear = false;
        }

        if (!settings.LightShaftsEnabled || !(sun.Fade > 0f) || !(settings.LightShaftsIntensity > 0f))
            return false;

        var extent = _mask.Extent;
        var push = new ShaftsPush
        {
            SunUv = sun.ScreenUv,
            Aspect = (float)extent.Width / extent.Height,
            Radius = MaskRadius,
            LuminanceCap = LuminanceCap,
            Taps = (uint)settings.LightShaftsTapsPerPass,
        };
        Pass(cb, _mask, _maskPipeline, _maskSet, ref push);
        (push.Step, push.TapDecay) = settings.LightShaftsPass(0);
        Pass(cb, _fine, _blurPipeline, _fineSet, ref push);
        (push.Step, push.TapDecay) = settings.LightShaftsPass(1);
        Pass(cb, _coarse, _blurPipeline, _coarseSet, ref push);
        return true;
    }

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, DescriptorSet source, ref ShaftsPush push)
    {
        var vk = _ctx.Vk;
        var extent = target.Extent;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &source, 0, null);
        fixed (ShaftsPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ShaftsPush), p);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void WriteSets(ImageView sceneView, ImageView depthView)
    {
        PipelineBuilder.WriteImage(_ctx, _maskSet, 0, Image(_pointSampler, sceneView, ImageLayout.ShaderReadOnlyOptimal));
        var depth = Image(_pointSampler, depthView, ImageLayout.DepthStencilReadOnlyOptimal);
        PipelineBuilder.WriteImage(_ctx, _maskSet, 1, depth);
        // The blur reads binding 0 only; binding 1 is written so every set is complete.
        PipelineBuilder.WriteImage(_ctx, _fineSet, 0, Image(_blurSampler, _mask.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal));
        PipelineBuilder.WriteImage(_ctx, _fineSet, 1, depth);
        PipelineBuilder.WriteImage(_ctx, _coarseSet, 0, Image(_blurSampler, _fine.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal));
        PipelineBuilder.WriteImage(_ctx, _coarseSet, 1, depth);
    }

    private static DescriptorImageInfo Image(Sampler sampler, ImageView view, ImageLayout layout) =>
        new() { Sampler = sampler, ImageView = view, ImageLayout = layout };

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _maskPipeline, null);
        vk.DestroyPipeline(_ctx.Device, _blurPipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _pointSampler, null);
        vk.DestroySampler(_ctx.Device, _blurSampler, null);
        _mask.Dispose();
        _fine.Dispose();
        _coarse.Dispose();
    }

    // LightShaftsMask/LightShaftsBlur.vk.frag's push block (std430).
    private struct ShaftsPush
    {
        public Vector2 SunUv;
        public float Aspect;
        public float Radius;
        public float LuminanceCap;
        public float Step;
        public float TapDecay;
        public uint Taps;
    }
}
