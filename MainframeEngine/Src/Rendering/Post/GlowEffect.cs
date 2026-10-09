using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Godot 4.7's glow blur chain (ADR 0124; <c>copy_effects.cpp</c> <c>gaussian_glow</c> + <c>copy.glsl</c>) as raster
/// passes: level k (k = 0..6) is the scene at <c>1 / 2^(k+1)</c> resolution, each built from the level before it by a
/// horizontal pass (downsample + 9-tap blur, into <c>temp[k]</c>) and a vertical pass (9-tap blur + strength, and on
/// level 0 the firefly undo, exposure (× auto exposure's when it is on, ADR 0154), HDR threshold and luminance cap, into
/// <c>level[k]</c>). Only the levels up to the
/// highest weighted one are drawn each frame (Godot's <c>max_glow_index</c>); every level image exists and is cleared
/// once after creation, so the tonemap pass can bind all seven. A <see cref="PostStage.BeforeTonemap"/> effect (ADR 0163)
/// after auto exposure, enabled whenever the post tonemap pass runs (it binds every level either way); allocates nothing
/// per frame.
/// </summary>
internal sealed unsafe class GlowEffect(AutoExposure autoExposure) : PostEffect("glow", PostStage.BeforeTonemap, PostEffectOrder.Glow)
{
    public const int LevelCount = PostProcessSettings.GlowLevelCount;
    private const Format LevelFormat = Format.R16G16B16A16Sfloat;

    private IVulkanContext _ctx = null!;
    private readonly RenderTarget[] _temp = new RenderTarget[LevelCount];
    private readonly RenderTarget[] _levels = new RenderTarget[LevelCount];
    private readonly DescriptorSet[] _tempSets = new DescriptorSet[LevelCount];
    private readonly DescriptorSet[] _levelSets = new DescriptorSet[LevelCount];
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _sceneSet;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private DescriptorImageInfo _adaptedLuminance;
    private bool _needsClear = true;

    /// <summary>Runs whenever the post tonemap pass does: the tonemap binds every level either way.</summary>
    public override bool IsEnabled(in PostEffectSettings settings) => settings.PostTonemap;

    protected override void OnCreate(PostEffectContext context)
    {
        autoExposure.Create(context); // its adapted luminance is read by the first level
        var ctx = context.Vulkan;
        var sceneExtent = context.Scene.Extent;
        var sceneView = context.Scene.Color;
        _ctx = ctx;
        _adaptedLuminance = autoExposure.AdaptedDescriptor;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (glow)");

        for (var k = 0; k < LevelCount; k++)
        {
            var size = LevelExtent(sceneExtent, k);
            _temp[k] = new RenderTarget(ctx, new RenderTargetDesc($"glow temp {k}", [RenderTargetAttachment.Sampled(LevelFormat)], null), size);
            _levels[k] = new RenderTarget(ctx, new RenderTargetDesc($"glow level {k}", [RenderTargetAttachment.Sampled(LevelFormat)], null), size);
        }

        // Binding 1: auto exposure's adapted luminance (ADR 0154), read by the first level's vertical pass.
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "glow");
        const uint sets = 1 + 2 * LevelCount;
        _pool = PipelineBuilder.CreatePool(ctx, sets, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * sets }], "glow");
        _sceneSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow scene");
        for (var k = 0; k < LevelCount; k++)
        {
            _tempSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow temp");
            _levelSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow level");
        }

        WriteSets(sceneView);
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(GlowPush), ShaderStageFlags.FragmentBit, "glow");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _temp[0].RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/GlowBlur.vk.frag.spv", [], [], "glow");
    }

    /// <summary>The sampler the tonemap reads the levels with (linear, clamp to edge).</summary>
    public Sampler Sampler => _sampler;

    /// <summary>Level <paramref name="k"/>'s image, in shader-read layout once <see cref="Record"/> has run.</summary>
    public ImageView LevelView(int k) => _levels[k].GetColor(0).View;

    /// <summary>Godot's BLUR_1 mip k: half the scene size, halved per level, at least one pixel.</summary>
    public static Extent2D LevelExtent(Extent2D scene, int k) =>
        new(Math.Max(1u, scene.Width >> (k + 1)), Math.Max(1u, scene.Height >> (k + 1)));

    /// <summary>After a swapchain resize (device idle): resized levels, rewritten sets; they are cleared again.</summary>
    protected override void OnResize(PostEffectContext context)
    {
        var sceneExtent = context.Scene.Extent;
        var sceneView = context.Scene.Color;
        for (var k = 0; k < LevelCount; k++)
        {
            var size = LevelExtent(sceneExtent, k);
            _temp[k].Resize(size);
            _levels[k].Resize(size);
        }

        WriteSets(sceneView);
        _needsClear = true;
    }

    protected override void OnRecord(PostEffectContext context) =>
        Record(context.CommandBuffer, context.Settings.World, context.Exposure);

    /// <summary>Records the blur chain for <paramref name="settings"/> (nothing when its glow is off, after the first clear).</summary>
    private void Record(CommandBuffer cb, in PostProcessSettings settings, float exposure)
    {
        if (_needsClear)
        {
            // Every level the tonemap binds must hold defined data in shader-read layout, drawn this frame or not.
            for (var k = 0; k < LevelCount; k++)
            {
                _temp[k].Begin(cb, default);
                _temp[k].End(cb);
                _levels[k].Begin(cb, default);
                _levels[k].End(cb);
            }

            _needsClear = false;
        }

        var max = settings.GlowMaxLevel;
        for (var k = 0; k <= max; k++)
        {
            var first = k == 0;
            var push = new GlowPush
            {
                Strength = settings.GlowStrength,
                Exposure = exposure,
                Threshold = settings.GlowHdrThreshold,
                Scale = settings.GlowHdrScale,
                Bloom = settings.GlowBloom,
                LuminanceCap = settings.GlowHdrLuminanceCap,
                AutoExposureScale = settings.AutoExposureScale,
            };
            var autoExposure = settings.AutoExposureEnabled ? 4u : 0u;
            push.Flags = 1u | (first ? 2u : 0u);
            Pass(cb, _temp[k], first ? _sceneSet : _levelSets[k - 1], ref push);
            push.Flags = (first ? 2u : 0u) | autoExposure;
            Pass(cb, _levels[k], _tempSets[k], ref push);
        }
    }

    private void Pass(CommandBuffer cb, RenderTarget target, DescriptorSet source, ref GlowPush push)
    {
        var vk = _ctx.Vk;
        var extent = target.Extent;
        push.DstWidth = extent.Width;
        push.DstHeight = extent.Height;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &source, 0, null);
        fixed (GlowPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(GlowPush), p);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void WriteSets(ImageView sceneView)
    {
        Write(_sceneSet, sceneView);
        for (var k = 0; k < LevelCount; k++)
        {
            Write(_tempSets[k], _temp[k].GetColor(0).View);
            Write(_levelSets[k], _levels[k].GetColor(0).View);
        }
    }

    private void Write(DescriptorSet set, ImageView view)
    {
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        PipelineBuilder.WriteImage(_ctx, set, 1, _adaptedLuminance);
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
        for (var k = 0; k < LevelCount; k++)
        {
            _temp[k].Dispose();
            _levels[k].Dispose();
        }
    }

    // GlowBlur.vk.frag's push block (std430).
    private struct GlowPush
    {
        public float DstWidth;
        public float DstHeight;
        public uint Flags;
        public float Strength;
        public float Exposure;
        public float Threshold;
        public float Scale;
        public float Bloom;
        public float LuminanceCap;
        public float AutoExposureScale;
    }
}
